#include "xpdf_winui_native.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <map>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <vector>

#ifdef _WIN32
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#endif

#include "ErrorCodes.h"
#include "GList.h"
#include "GlobalParams.h"
#include "GString.h"
#include "Outline.h"
#include "PDFDoc.h"
#include "SplashBitmap.h"
#include "SplashOutputDev.h"
#include "TextString.h"
#include "TextOutputDev.h"
#include "gmem.h"

namespace {

const int maxBitmapDimension = 16384;
const size_t maxBitmapPixels = 32 * 1024 * 1024;
const size_t maxTextBytes = 16 * 1024 * 1024;
thread_local char lastError[512] = {};

int fail(int status, const char *message) noexcept {
  std::snprintf(lastError, sizeof(lastError), "%s", message);
  return status;
}

struct Engine {
  std::mutex mutex;
  std::unique_ptr<GlobalParams> params;
  // Xpdf owns process-wide configuration and caches, so every use of the
  // engine is serialized through this one bridge instance.
  struct Document {
    std::unique_ptr<PDFDoc> pdf;
  };
  std::map<void *, std::unique_ptr<Document>> documents;

  ~Engine() {
    documents.clear();
    params.reset();
    globalParams = nullptr;
  }

  void initialize() {
    if (params) {
      return;
    }
    std::unique_ptr<GlobalParams> candidate(new GlobalParams(nullptr));
    globalParams = candidate.get();
    try {
      candidate->setErrQuiet(gTrue);
      candidate->setTextEncoding("UTF-8");
      char unixEol[] = "unix";
      candidate->setTextEOL(unixEol);
      candidate->setTextPageBreaks(gFalse);
      candidate->setupBaseFonts(nullptr);
    } catch (...) {
      globalParams = nullptr;
      throw;
    }
    params = std::move(candidate);
  }
};

Engine &engine() {
  static Engine instance;
  return instance;
}

template<typename Operation>
int guarded(Operation operation, int exceptionResult = XPDF_ERROR) noexcept {
  lastError[0] = '\0';
  try {
    Engine &state = engine();
    std::lock_guard<std::mutex> lock(state.mutex);
    return operation(state);
  } catch (const GMemException &) {
    fail(XPDF_ERROR, "Xpdf could not allocate enough memory.");
  } catch (const std::bad_alloc &) {
    fail(XPDF_ERROR, "Not enough memory to complete the operation.");
  } catch (const std::exception &) {
    fail(XPDF_ERROR, "The native PDF operation failed.");
  } catch (...) {
    fail(XPDF_ERROR, "An unexpected native PDF error occurred.");
  }
  return exceptionResult;
}

PDFDoc *findDocument(Engine &state, void *handle) {
  const auto found = state.documents.find(handle);
  if (found == state.documents.end()) {
    fail(XPDF_INVALID_ARGUMENT, "The document handle is invalid or closed.");
    return nullptr;
  }
  return found->second->pdf.get();
}

bool validPage(PDFDoc *document, int page) {
  if (page < 1 || page > document->getNumPages()) {
    fail(XPDF_INVALID_ARGUMENT, "The page number is outside the document.");
    return false;
  }
  Page *pdfPage = document->getCatalog()->getPage(page);
  if (!pdfPage || !pdfPage->isOk()) {
    fail(XPDF_ERROR, "The PDF page could not be read.");
    return false;
  }
  return true;
}

bool validRotation(int rotation) {
  return rotation == 0 || rotation == 90 || rotation == 180 || rotation == 270;
}

int pageSize(PDFDoc *document, int page, int rotation,
             double &width, double &height) {
  if (!validRotation(rotation)) {
    return fail(XPDF_INVALID_ARGUMENT, "Rotation must be 0, 90, 180 or 270.");
  }
  if (!validPage(document, page)) {
    return page < 1 || page > document->getNumPages() ? XPDF_INVALID_ARGUMENT
                                                    : XPDF_ERROR;
  }
  width = document->getPageCropWidth(page);
  height = document->getPageCropHeight(page);
  if ((document->getPageRotate(page) + rotation) % 180 != 0) {
    std::swap(width, height);
  }
  if (!std::isfinite(width) || !std::isfinite(height) || width <= 0 || height <= 0) {
    return fail(XPDF_ERROR, "The PDF page has invalid dimensions.");
  }
  return XPDF_SUCCESS;
}

bool bitmapFits(double width, double height) {
  return std::isfinite(width) && std::isfinite(height) && width >= 1 && height >= 1
      && width <= maxBitmapDimension && height <= maxBitmapDimension
      && width * height <= static_cast<double>(maxBitmapPixels);
}

int copyBitmapToBgra(SplashBitmap *bitmap, unsigned char **pixels, int *width,
                     int *height, int *stride) {
  if (!bitmap || !bitmap->getDataPtr() || bitmap->getWidth() <= 0
      || bitmap->getHeight() <= 0
      || !bitmapFits(bitmap->getWidth(), bitmap->getHeight())) {
    return fail(XPDF_ERROR, "Xpdf could not produce a valid page bitmap.");
  }
  const int outputWidth = bitmap->getWidth();
  const int outputHeight = bitmap->getHeight();
  const int outputStride = outputWidth * 4;
  const size_t bytes = static_cast<size_t>(outputStride) * outputHeight;
  std::unique_ptr<unsigned char, decltype(&std::free)> result(
      static_cast<unsigned char *>(std::malloc(bytes)), &std::free);
  if (!result) {
    return fail(XPDF_ERROR, "Not enough memory for the page bitmap.");
  }
  for (int y = 0; y < outputHeight; ++y) {
    const unsigned char *source = bitmap->getDataPtr() + y * bitmap->getRowSize();
    unsigned char *destination = result.get() + static_cast<size_t>(y) * outputStride;
    for (int x = 0; x < outputWidth; ++x) {
      destination[4 * x] = source[3 * x];
      destination[4 * x + 1] = source[3 * x + 1];
      destination[4 * x + 2] = source[3 * x + 2];
      destination[4 * x + 3] = 255;
    }
  }
  *width = outputWidth;
  *height = outputHeight;
  *stride = outputStride;
  *pixels = result.release();
  return XPDF_SUCCESS;
}

struct TextBuffer {
  std::string text;
  bool failed = false;
};

void appendText(void *context, const char *text, int length) noexcept {
  TextBuffer &buffer = *static_cast<TextBuffer *>(context);
  if (buffer.failed || length <= 0) {
    return;
  }
  if (static_cast<size_t>(length) > maxTextBytes - buffer.text.size()) {
    buffer.failed = true;
    return;
  }
  try {
    buffer.text.append(text, static_cast<size_t>(length));
  } catch (...) {
    buffer.failed = true;
  }
}

#ifndef DISABLE_OUTLINE
void writeOutline(PDFDoc *pdf, OutlineItem *item, int depth, std::string &out) {
  TextString *titleText = item->getTitleTextString();
  std::unique_ptr<GString> title(titleText ? titleText->toUTF8() : new GString(""));
  const int page = pdf->getOutlineTargetPage(item);
  out += std::to_string(depth);
  out += '\x1F';
  out += std::to_string(page);
  out += '\x1F';
  out += title->getCString();
  out += '\n';
  if (item->hasKids()) {
    GList *kids = item->getKids();
    if (kids) {
      for (int index = 0; index < kids->getLength(); ++index) {
        if (OutlineItem *child = static_cast<OutlineItem *>(kids->get(index))) {
          writeOutline(pdf, child, depth + 1, out);
        }
      }
    }
  }
}
#endif

} // namespace

int XPDF_CALL xpdf_open_document(const char *utf8Path, const char *utf8Password,
                                void **document) {
  if (document) {
    *document = nullptr;
  }
  return guarded([&](Engine &state) -> int {
    if (!document || !utf8Path || !*utf8Path) {
      return fail(XPDF_INVALID_ARGUMENT, "A PDF path and output handle are required.");
    }
    state.initialize();
    std::unique_ptr<GString> password;
    if (utf8Password) {
      password.reset(new GString(utf8Password));
    }
    std::unique_ptr<PDFDoc> pdf;
#ifdef _WIN32
    const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                        utf8Path, -1, nullptr, 0);
    if (size <= 1 || size > 32768) {
      return fail(XPDF_INVALID_ARGUMENT, "The PDF path is not valid UTF-8 or is too long.");
    }
    std::vector<wchar_t> path(static_cast<size_t>(size));
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8Path, -1,
                             path.data(), size)) {
      return fail(XPDF_INVALID_ARGUMENT, "The PDF path could not be converted to UTF-16.");
    }
    pdf.reset(new PDFDoc(path.data(), size - 1, password.get(), password.get()));
#else
    std::string path(utf8Path);
    pdf.reset(new PDFDoc(&path[0], password.get(), password.get()));
#endif
    if (!pdf->isOk()) {
      switch (pdf->getErrorCode()) {
      case errEncrypted:
        return fail(XPDF_PASSWORD_REQUIRED, "A valid PDF password is required.");
      case errPermission:
        return fail(XPDF_PERMISSION_DENIED, "The PDF does not permit this operation.");
      case errOpenFile:
        return fail(XPDF_ERROR, "The PDF file could not be opened.");
      default:
        return fail(XPDF_ERROR, "The file is not a readable PDF document.");
      }
    }
    if (pdf->getNumPages() < 1) {
      return fail(XPDF_ERROR, "The PDF document contains no pages.");
    }
    std::unique_ptr<Engine::Document> nativeDocument(new Engine::Document());
    nativeDocument->pdf = std::move(pdf);
    void *handle = nativeDocument.get();
    state.documents[handle] = std::move(nativeDocument);
    *document = handle;
    return XPDF_SUCCESS;
  });
}

void XPDF_CALL xpdf_close_document(void *document) {
  if (!document) {
    return;
  }
  try {
    Engine &state = engine();
    std::lock_guard<std::mutex> lock(state.mutex);
    const auto found = state.documents.find(document);
    if (found == state.documents.end()) {
      fail(XPDF_INVALID_ARGUMENT, "The document handle is invalid or closed.");
      return;
    }
    state.documents.erase(found);
  } catch (...) {
    fail(XPDF_ERROR, "The native document could not be closed.");
  }
}

int XPDF_CALL xpdf_get_page_count(void *document) {
  return guarded([&](Engine &state) -> int {
    PDFDoc *pdf = findDocument(state, document);
    return pdf ? pdf->getNumPages() : 0;
  }, 0);
}

int XPDF_CALL xpdf_get_page_size(void *document, int page, int rotation,
                                double *widthPoints, double *heightPoints) {
  if (widthPoints) {
    *widthPoints = 0;
  }
  if (heightPoints) {
    *heightPoints = 0;
  }
  return guarded([&](Engine &state) -> int {
    if (!widthPoints || !heightPoints) {
      return fail(XPDF_INVALID_ARGUMENT, "Page dimension outputs are required.");
    }
    PDFDoc *pdf = findDocument(state, document);
    if (!pdf) {
      return XPDF_INVALID_ARGUMENT;
    }
    double width, height;
    const int status = pageSize(pdf, page, rotation, width, height);
    if (status == XPDF_SUCCESS) {
      *widthPoints = width;
      *heightPoints = height;
    }
    return status;
  });
}

int XPDF_CALL xpdf_get_page_sizes(void *document, int rotation,
                                  double *widthPoints, double *heightPoints,
                                  int count) {
  return guarded([&](Engine &state) -> int {
    if (!widthPoints || !heightPoints || count < 1) {
      return fail(XPDF_INVALID_ARGUMENT,
                  "Page size buffers and a positive count are required.");
    }
    PDFDoc *pdf = findDocument(state, document);
    if (!pdf) {
      return XPDF_INVALID_ARGUMENT;
    }
    if (!validRotation(rotation)) {
      return fail(XPDF_INVALID_ARGUMENT, "Rotation must be 0, 90, 180 or 270.");
    }
    if (count > pdf->getNumPages()) {
      return fail(XPDF_INVALID_ARGUMENT, "The count exceeds the page count.");
    }
    for (int page = 1; page <= count; ++page) {
      Page *pdfPage = pdf->getCatalog()->getPage(page);
      if (!pdfPage || !pdfPage->isOk()) {
        return fail(XPDF_ERROR, "The PDF page could not be read.");
      }
      double width = pdfPage->getCropWidth();
      double height = pdfPage->getCropHeight();
      if ((pdfPage->getRotate() + rotation) % 180 != 0) {
        std::swap(width, height);
      }
      if (!std::isfinite(width) || !std::isfinite(height) || width <= 0 || height <= 0) {
        return fail(XPDF_ERROR, "The PDF page has invalid dimensions.");
      }
      widthPoints[page - 1] = width;
      heightPoints[page - 1] = height;
    }
    return XPDF_SUCCESS;
  });
}

int XPDF_CALL xpdf_render_page(void *document, int page, double dpi, int rotation,
                              unsigned char **pixels, int *width, int *height,
                              int *stride) {
  if (pixels) {
    *pixels = nullptr;
  }
  if (width) {
    *width = 0;
  }
  if (height) {
    *height = 0;
  }
  if (stride) {
    *stride = 0;
  }
  return guarded([&](Engine &state) -> int {
    if (!pixels || !width || !height || !stride || !std::isfinite(dpi)
        || dpi < 1 || dpi > 1200) {
      return fail(XPDF_INVALID_ARGUMENT, "Bitmap outputs and a DPI from 1 to 1200 are required.");
    }
    PDFDoc *pdf = findDocument(state, document);
    if (!pdf) {
      return XPDF_INVALID_ARGUMENT;
    }
    double widthPoints, heightPoints;
    const int status = pageSize(pdf, page, rotation, widthPoints, heightPoints);
    if (status != XPDF_SUCCESS) {
      return status;
    }
    const double pixelWidth = std::max(1.0, std::floor(widthPoints * dpi / 72 + 0.5));
    const double pixelHeight = std::max(1.0, std::floor(heightPoints * dpi / 72 + 0.5));
    if (!bitmapFits(pixelWidth, pixelHeight)) {
      return fail(XPDF_INVALID_ARGUMENT, "The requested bitmap is too large; reduce the zoom.");
    }
    SplashColor paper = {255, 255, 255};
    SplashOutputDev output(splashModeBGR8, 4, gFalse, paper, gTrue);
    output.startDoc(pdf->getXRef());
    pdf->displayPage(&output, nullptr, page, dpi, dpi, rotation,
                     gFalse, gTrue, gFalse);
    return copyBitmapToBgra(output.getBitmap(), pixels, width, height, stride);
  });
}

int XPDF_CALL xpdf_render_page_tile(
    void *document, int page, double dpi, int rotation, int sliceX, int sliceY,
    int sliceWidth, int sliceHeight, unsigned char **pixels, int *width,
    int *height, int *stride) {
  if (pixels) {
    *pixels = nullptr;
  }
  if (width) {
    *width = 0;
  }
  if (height) {
    *height = 0;
  }
  if (stride) {
    *stride = 0;
  }
  return guarded([&](Engine &state) -> int {
    if (!pixels || !width || !height || !stride || !std::isfinite(dpi)
        || dpi < 1 || dpi > 1200 || !validRotation(rotation)
        || sliceX < 0 || sliceY < 0 || sliceWidth <= 0 || sliceHeight <= 0) {
      return fail(XPDF_INVALID_ARGUMENT,
                  "Bitmap outputs, DPI from 1 to 1200, and a positive tile are required.");
    }
    PDFDoc *pdf = findDocument(state, document);
    if (!pdf) {
      return XPDF_INVALID_ARGUMENT;
    }
    double pageWidthPoints, pageHeightPoints;
    const int status = pageSize(pdf, page, rotation, pageWidthPoints, pageHeightPoints);
    if (status != XPDF_SUCCESS) {
      return status;
    }
    const double fullPixelWidth =
        std::max(1.0, std::floor(pageWidthPoints * dpi / 72 + 0.5));
    const double fullPixelHeight =
        std::max(1.0, std::floor(pageHeightPoints * dpi / 72 + 0.5));
    if (!bitmapFits(fullPixelWidth, fullPixelHeight)) {
      return fail(XPDF_INVALID_ARGUMENT, "The requested page is too large; reduce the zoom.");
    }
    const int fullWidth = static_cast<int>(fullPixelWidth);
    const int fullHeight = static_cast<int>(fullPixelHeight);
    if (sliceX > fullWidth || sliceY > fullHeight
        || sliceWidth > fullWidth - sliceX
        || sliceHeight > fullHeight - sliceY) {
      return fail(XPDF_INVALID_ARGUMENT, "The requested tile is outside the page bitmap.");
    }
    if (!bitmapFits(sliceWidth, sliceHeight)) {
      return fail(XPDF_INVALID_ARGUMENT, "The requested tile is too large.");
    }

    SplashColor paper = {255, 255, 255};
    SplashOutputDev output(splashModeBGR8, 4, gFalse, paper, gTrue);
    output.startDoc(pdf->getXRef());
    pdf->displayPageSlice(&output, nullptr, page, dpi, dpi, rotation,
                          gFalse, gTrue, gFalse, sliceX, sliceY,
                          sliceWidth, sliceHeight);
    return copyBitmapToBgra(output.getBitmap(), pixels, width, height, stride);
  });
}

void XPDF_CALL xpdf_free_buffer(void *buffer) {
  std::free(buffer);
}

int XPDF_CALL xpdf_get_outline(void *document, char **utf8Lines) {
  if (utf8Lines) {
    *utf8Lines = nullptr;
  }
  return guarded([&](Engine &state) -> int {
    if (!utf8Lines) {
      return fail(XPDF_INVALID_ARGUMENT, "An outline output pointer is required.");
    }
    PDFDoc *pdf = findDocument(state, document);
    if (!pdf) {
      return XPDF_INVALID_ARGUMENT;
    }
    std::string serialized;
#ifdef DISABLE_OUTLINE
    (void)pdf;
#else
    Outline *outline = pdf->getOutline();
    if (outline) {
      GList *items = outline->getItems();
      if (items) {
        for (int index = 0; index < items->getLength(); ++index) {
          if (OutlineItem *item = static_cast<OutlineItem *>(items->get(index))) {
            writeOutline(pdf, item, 0, serialized);
          }
        }
      }
    }
#endif
    char *result = static_cast<char *>(std::malloc(serialized.size() + 1));
    if (!result) {
      return fail(XPDF_ERROR, "Not enough memory for the document outline.");
    }
    std::memcpy(result, serialized.c_str(), serialized.size() + 1);
    *utf8Lines = result;
    return XPDF_SUCCESS;
  });
}

int XPDF_CALL xpdf_get_page_text(void *document, int page, char **utf8Text) {
  if (utf8Text) {
    *utf8Text = nullptr;
  }
  return guarded([&](Engine &state) -> int {
    if (!utf8Text) {
      return fail(XPDF_INVALID_ARGUMENT, "A text output pointer is required.");
    }
    PDFDoc *pdf = findDocument(state, document);
    if (!pdf) {
      return XPDF_INVALID_ARGUMENT;
    }
    if (!validPage(pdf, page)) {
      return page < 1 || page > pdf->getNumPages() ? XPDF_INVALID_ARGUMENT : XPDF_ERROR;
    }
    if (!pdf->okToCopy()) {
      return fail(XPDF_PERMISSION_DENIED, "This PDF does not permit text copying.");
    }
    TextOutputControl control;
    control.mode = textOutReadingOrder;
    TextBuffer buffer;
    TextOutputDev output(static_cast<char *>(nullptr), &control, gFalse);
    pdf->displayPage(&output, nullptr, page, 72, 72, 0, gFalse, gTrue, gFalse);
    // Serialize text outside Gfx's destructor so allocation errors are catchable.
    std::unique_ptr<TextPage> textPage(output.takeText());
    textPage->write(&buffer, &appendText);
    if (buffer.failed) {
      return fail(XPDF_ERROR, "The page text exceeds the memory limit or could not be allocated.");
    }
    char *result = static_cast<char *>(std::malloc(buffer.text.size() + 1));
    if (!result) {
      return fail(XPDF_ERROR, "Not enough memory for the page text.");
    }
    std::memcpy(result, buffer.text.c_str(), buffer.text.size() + 1);
    *utf8Text = result;
    return XPDF_SUCCESS;
  });
}

const char *XPDF_CALL xpdf_get_last_error(void) {
  return lastError;
}
