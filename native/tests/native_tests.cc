#include "xpdf_winui_native.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <limits>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#  include <bcrypt.h>
#endif

namespace {

void require(bool condition, const char *message) {
  if (!condition) {
    throw std::runtime_error(std::string(message) + ": " + xpdf_get_last_error());
  }
}

std::string serializePdf(const std::vector<std::string> &objects,
                         const std::string &trailer = "") {
  std::ostringstream pdf;
  pdf << "%PDF-1.4\n";
  std::vector<std::streamoff> offsets;
  for (size_t i = 0; i < objects.size(); ++i) {
    offsets.push_back(pdf.tellp());
    pdf << i + 1 << " 0 obj\n" << objects[i] << "\nendobj\n";
  }
  const auto xref = pdf.tellp();
  pdf << "xref\n0 " << objects.size() + 1 << "\n0000000000 65535 f \n";
  for (const auto offset : offsets) {
    pdf << std::setw(10) << std::setfill('0') << offset << " 00000 n \n";
  }
  pdf << "trailer\n<< /Size " << objects.size() + 1 << " /Root 1 0 R " << trailer << ">>\n"
      << "startxref\n" << xref << "\n%%EOF\n";
  return pdf.str();
}

std::string makePdf() {
  const std::string content =
      "1 0 0 rg 0 100 200 100 re f\n"
      "0 0 1 rg 0 0 200 100 re f\n"
      "0 0 0 rg BT /F1 18 Tf 20 90 Td (Hello WinUI) Tj ET\n";
  return serializePdf({
      "<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R >>",
      "<< /Type /Pages /Kids [3 0 R 6 0 R 7 0 R] /Count 3 >>",
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] "
      "/CropBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> "
      "/Contents 5 0 R >>",
      "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
      "<< /Length " + std::to_string(content.size()) + " >>\nstream\n" + content + "endstream",
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] "
      "/Rotate 90 /Resources << >> >>",
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2000 2000] /Resources << >> >>",
      "<< /Type /Outlines /First 9 0 R /Last 10 0 R /Count 2 >>",
      "<< /Title (Chapter 1) /Parent 8 0 R /Next 10 0 R /Dest [3 0 R /Fit] >>",
      "<< /Title (Chapter 2) /Parent 8 0 R /Prev 9 0 R /Dest [7 0 R /Fit] >>"
  });
}

#ifdef _WIN32
struct CryptoContext {
  BCRYPT_ALG_HANDLE algorithm = nullptr;
  BCRYPT_HASH_HANDLE hash = nullptr;
  BCRYPT_KEY_HANDLE key = nullptr;
  ~CryptoContext() {
    if (hash) { BCryptDestroyHash(hash); }
    if (key) { BCryptDestroyKey(key); }
    if (algorithm) { BCryptCloseAlgorithmProvider(algorithm, 0); }
  }
};

PUCHAR cryptoBytes(const std::string &bytes) {
  return reinterpret_cast<PUCHAR>(const_cast<char *>(bytes.data()));
}

std::string md5(const std::string &bytes) {
  std::vector<unsigned char> hashObject;
  CryptoContext context;
  require(BCryptOpenAlgorithmProvider(&context.algorithm, BCRYPT_MD5_ALGORITHM,
                                      nullptr, 0) >= 0, "open fixture hash algorithm");
  ULONG objectLength = 0, received = 0;
  require(BCryptGetProperty(context.algorithm, BCRYPT_OBJECT_LENGTH,
                            reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength),
                            &received, 0) >= 0, "get fixture hash storage");
  hashObject.resize(objectLength);
  require(BCryptCreateHash(context.algorithm, &context.hash, hashObject.data(),
                           objectLength, nullptr, 0, 0) >= 0, "create fixture hash");
  require(BCryptHashData(context.hash, cryptoBytes(bytes), static_cast<ULONG>(bytes.size()), 0) >= 0,
          "hash fixture password");
  std::string result(16, '\0');
  require(BCryptFinishHash(context.hash, cryptoBytes(result), 16, 0) >= 0,
          "finish fixture hash");
  return result;
}

std::string rc4(const std::string &key, const std::string &bytes) {
  std::vector<unsigned char> keyObject;
  CryptoContext context;
  require(BCryptOpenAlgorithmProvider(&context.algorithm, BCRYPT_RC4_ALGORITHM,
                                      nullptr, 0) >= 0, "open fixture cipher");
  ULONG objectLength = 0, received = 0;
  require(BCryptGetProperty(context.algorithm, BCRYPT_OBJECT_LENGTH,
                            reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength),
                            &received, 0) >= 0, "get fixture key storage");
  keyObject.resize(objectLength);
  require(BCryptGenerateSymmetricKey(context.algorithm, &context.key, keyObject.data(),
                                    objectLength, cryptoBytes(key), static_cast<ULONG>(key.size()),
                                    0) >= 0, "create fixture key");
  std::string result(bytes.size(), '\0');
  require(BCryptEncrypt(context.key, cryptoBytes(bytes), static_cast<ULONG>(bytes.size()),
                        nullptr, nullptr, 0, cryptoBytes(result), static_cast<ULONG>(result.size()),
                        &received, 0) >= 0 && received == result.size(), "encrypt fixture password");
  return result;
}

std::string hex(const std::string &bytes) {
  std::ostringstream text;
  for (const auto byte : bytes) {
    text << std::hex << std::setw(2) << std::setfill('0') << static_cast<int>(static_cast<unsigned char>(byte));
  }
  return text.str();
}

std::string paddedPassword(const std::string &password) {
  const char padding[] =
      "\x28\xbf\x4e\x5e\x4e\x75\x8a\x41\x64\x00\x4e\x56\xff\xfa\x01\x08"
      "\x2e\x2e\x00\xb6\xd0\x68\x3e\x80\x2f\x0c\xa9\xfe\x64\x53\x69\x7a";
  return (password + std::string(padding, 32)).substr(0, 32);
}

std::string makeEncryptedPdf() {
  // Standard Security Handler revision 2, with copy permission disabled.
  const std::string id = "0123456789abcdef";
  const std::string owner = rc4(md5(paddedPassword("owner")).substr(0, 5), paddedPassword("reader"));
  const std::string key = md5(paddedPassword("reader") + owner + "\xec\xff\xff\xff" + id).substr(0, 5);
  const std::string user = rc4(key, paddedPassword(""));
  return serializePdf({
      "<< /Type /Catalog /Pages 2 0 R >>",
      "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
      "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>",
      "<< /Filter /Standard /V 1 /R 2 /Length 40 /O <" + hex(owner) + "> /U <" + hex(user) + "> /P -20 >>"
  }, "/Encrypt 4 0 R /ID [<" + hex(id) + "><" + hex(id) + ">] ");
}
#endif

struct Fixture {
  std::string path;
#ifdef _WIN32
  std::wstring widePath;
#endif

  Fixture(const std::string &filename = "xpdf-winui-\xE6\xB5\x8B\xE8\xAF\x95-\xF0\x9F\x93\x84.pdf",
          const std::string &bytes = makePdf()) : path(filename) {
    FILE *file = nullptr;
#ifdef _WIN32
    const int size = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, nullptr, 0);
    std::vector<wchar_t> converted(static_cast<size_t>(size));
    MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, converted.data(), size);
    widePath.assign(converted.data());
    file = _wfopen(widePath.c_str(), L"wb");
#else
    file = std::fopen(path.c_str(), "wb");
#endif
    require(file != nullptr, "create Unicode PDF fixture");
    const auto written = std::fwrite(bytes.data(), 1, bytes.size(), file);
    const int closed = std::fclose(file);
    require(written == bytes.size() && closed == 0, "write fixture");
  }

  ~Fixture() {
#ifdef _WIN32
    _wremove(widePath.c_str());
#else
    std::remove(path.c_str());
#endif
  }
};

struct Document {
  void *handle = nullptr;
  ~Document() { xpdf_close_document(handle); }
};

struct Bitmap {
  unsigned char *pixels = nullptr;
  int width = 0, height = 0, stride = 0;
  ~Bitmap() { xpdf_free_buffer(pixels); }
};

void checkPixel(const Bitmap &bitmap, int x, int y, int blue, int green, int red,
                const char *label = nullptr) {
  const auto pixel = bitmap.pixels + y * bitmap.stride + x * 4;
  std::ostringstream message;
  message << (label ? label : "opaque top-down BGRA pixel") << " at " << x << "," << y
          << " expected " << blue << "," << green << "," << red
          << " got " << static_cast<int>(pixel[0]) << ","
          << static_cast<int>(pixel[1]) << ","
          << static_cast<int>(pixel[2]) << ","
          << static_cast<int>(pixel[3]);
  require(pixel[0] == blue && pixel[1] == green && pixel[2] == red && pixel[3] == 255,
          message.str().c_str());
}

void runTests() {
  Fixture fixture;
  Document doc;
  require(xpdf_open_document(fixture.path.c_str(), nullptr, &doc.handle) == XPDF_SUCCESS,
          "open Unicode path including a surrogate pair");
  require(xpdf_get_page_count(doc.handle) == 3, "page count");
  double width = 0, height = 0;
  require(xpdf_get_page_size(doc.handle, 1, 0, &width, &height) == XPDF_SUCCESS
          && width == 200 && height == 200, "CropBox dimensions");
  require(xpdf_get_page_size(doc.handle, 2, 0, &width, &height) == XPDF_SUCCESS
          && width == 100 && height == 200, "intrinsic rotation dimensions");
  require(xpdf_get_page_size(doc.handle, 2, 90, &width, &height) == XPDF_SUCCESS
          && width == 200 && height == 100, "additional rotation dimensions");

  std::vector<double> batchWidths(3), batchHeights(3);
  require(xpdf_get_page_sizes(doc.handle, 0, batchWidths.data(), batchHeights.data(), 3) == XPDF_SUCCESS,
          "batch page sizes");
  require(batchWidths[0] == 200 && batchHeights[0] == 200, "batch first page size");
  require(batchWidths[1] == 100 && batchHeights[1] == 200, "batch rotated page size");
  require(batchWidths[2] == 2000 && batchHeights[2] == 2000, "batch last page size");
  require(xpdf_get_page_sizes(doc.handle, 0, batchWidths.data(), batchHeights.data(), 4) == XPDF_INVALID_ARGUMENT,
          "reject batch count beyond page count");
  require(xpdf_get_page_sizes(doc.handle, 0, nullptr, batchHeights.data(), 3) == XPDF_INVALID_ARGUMENT,
          "reject batch missing width buffer");
  require(xpdf_get_page_sizes(doc.handle, 0, batchWidths.data(), batchHeights.data(), 0) == XPDF_INVALID_ARGUMENT,
          "reject zero batch count");
  require(xpdf_get_page_sizes(doc.handle, 45, batchWidths.data(), batchHeights.data(), 3) == XPDF_INVALID_ARGUMENT,
          "reject batch bad rotation");

  char *outline = nullptr;
  require(xpdf_get_outline(doc.handle, &outline) == XPDF_SUCCESS, "read outline");
  const std::string outlineText(outline ? outline : "");
  xpdf_free_buffer(outline);
  require(outlineText.find("Chapter 1") != std::string::npos
          && outlineText.find("Chapter 2") != std::string::npos, "outline titles");
  const std::string sep(1, '\x1F');
  require(outlineText.find(sep + "1" + sep + "Chapter 1") != std::string::npos,
          "outline chapter 1 page");
  require(outlineText.find(sep + "3" + sep + "Chapter 2") != std::string::npos,
          "outline chapter 2 page");
  require(xpdf_get_outline(doc.handle, nullptr) == XPDF_INVALID_ARGUMENT, "reject missing outline output");

  Bitmap bitmap;
  require(xpdf_render_page(doc.handle, 1, 72, 0, &bitmap.pixels,
                          &bitmap.width, &bitmap.height, &bitmap.stride) == XPDF_SUCCESS,
          "render page");
  require(bitmap.width == 200 && bitmap.height == 200 && bitmap.stride == 800,
          "rendered dimensions and stride");
  checkPixel(bitmap, 10, 10, 0, 0, 255);
  checkPixel(bitmap, 10, 190, 255, 0, 0);

  Bitmap rotated;
  require(xpdf_render_page(doc.handle, 1, 144, 90, &rotated.pixels,
                          &rotated.width, &rotated.height, &rotated.stride) == XPDF_SUCCESS,
          "render rotated scaled page");
  require(rotated.width == 400 && rotated.height == 400 && rotated.stride == 1600,
          "DPI scale");
  checkPixel(rotated, 10, 10, 255, 0, 0);
  checkPixel(rotated, 390, 10, 0, 0, 255);

  Bitmap blank;
  require(xpdf_render_page(doc.handle, 2, 72, 0, &blank.pixels,
                          &blank.width, &blank.height, &blank.stride) == XPDF_SUCCESS,
          "render blank intrinsically rotated page");
  require(blank.width == 100 && blank.height == 200, "intrinsic bitmap rotation");
  checkPixel(blank, 10, 10, 255, 255, 255);

  Bitmap topLeftTile;
  require(xpdf_render_page_tile(doc.handle, 1, 72, 0, 0, 0, 100, 100,
                                &topLeftTile.pixels, &topLeftTile.width,
                                &topLeftTile.height, &topLeftTile.stride) == XPDF_SUCCESS,
          "render top-left page tile");
  require(topLeftTile.width == 100 && topLeftTile.height == 100
          && topLeftTile.stride == 400, "tile dimensions and stride");
  checkPixel(topLeftTile, 10, 10, 0, 0, 255, "top-left tile pixel");

  Bitmap bottomRightTile;
  require(xpdf_render_page_tile(doc.handle, 1, 72, 0, 100, 100, 100, 100,
                                &bottomRightTile.pixels, &bottomRightTile.width,
                                &bottomRightTile.height, &bottomRightTile.stride) == XPDF_SUCCESS,
          "render bottom-right page tile");
  require(bottomRightTile.width == 100 && bottomRightTile.height == 100,
          "bottom-right tile dimensions");
  checkPixel(bottomRightTile, 10, 10, 255, 0, 0, "bottom-right tile pixel");

  Bitmap middleTile;
  require(xpdf_render_page_tile(doc.handle, 1, 72, 0, 50, 50, 100, 100,
                                &middleTile.pixels, &middleTile.width,
                                &middleTile.height, &middleTile.stride) == XPDF_SUCCESS,
          "render middle page tile");
  checkPixel(middleTile, 10, 10, 0, 0, 255, "middle tile top pixel");
  checkPixel(middleTile, 10, 90, 255, 0, 0, "middle tile bottom pixel");

  Bitmap rotatedTile;
  require(xpdf_render_page_tile(doc.handle, 1, 144, 90, 300, 0, 100, 100,
                                &rotatedTile.pixels, &rotatedTile.width,
                                &rotatedTile.height, &rotatedTile.stride) == XPDF_SUCCESS,
          "render rotated page tile");
  require(rotatedTile.width == 100 && rotatedTile.height == 100
          && rotatedTile.stride == 400, "rotated tile dimensions");
  checkPixel(rotatedTile, 10, 10, 0, 0, 255, "rotated tile pixel");

  Bitmap invalidTile;
  require(xpdf_render_page_tile(doc.handle, 1, 72, 0, -1, 0, 100, 100,
                                &invalidTile.pixels, &invalidTile.width,
                                &invalidTile.height, &invalidTile.stride) == XPDF_INVALID_ARGUMENT,
          "reject negative tile origin");
  require(xpdf_render_page_tile(doc.handle, 1, 72, 0, 150, 0, 100, 100,
                                &invalidTile.pixels, &invalidTile.width,
                                &invalidTile.height, &invalidTile.stride) == XPDF_INVALID_ARGUMENT,
          "reject tile outside page bitmap");
  require(xpdf_render_page_tile(doc.handle, 1, 72, 0, 0, 0, 0, 100,
                                &invalidTile.pixels, &invalidTile.width,
                                &invalidTile.height, &invalidTile.stride) == XPDF_INVALID_ARGUMENT,
          "reject zero tile dimension");
  require(xpdf_render_page_tile(doc.handle, 1, 0, 0, 0, 0, 100, 100,
                                &invalidTile.pixels, &invalidTile.width,
                                &invalidTile.height, &invalidTile.stride) == XPDF_INVALID_ARGUMENT,
          "reject invalid tile DPI");
  require(xpdf_render_page_tile(doc.handle, 1, 72, 45, 0, 0, 100, 100,
                                &invalidTile.pixels, &invalidTile.width,
                                &invalidTile.height, &invalidTile.stride) == XPDF_INVALID_ARGUMENT,
          "reject invalid tile rotation");
  require(!invalidTile.pixels && invalidTile.width == 0 && invalidTile.height == 0
          && invalidTile.stride == 0, "reset tile outputs after failure");

  char *text = nullptr;
  require(xpdf_get_page_text(doc.handle, 1, &text) == XPDF_SUCCESS, "extract text");
  const std::string extracted(text);
  xpdf_free_buffer(text);
  require(extracted.find("Hello WinUI") != std::string::npos, "text content");
  require(extracted.find('\r') == std::string::npos, "LF text line endings");
  require(xpdf_get_page_text(doc.handle, 2, &text) == XPDF_SUCCESS, "extract blank page");
  require(text[0] == '\0', "blank page text is empty");
  xpdf_free_buffer(text);

  for (const auto dpi : {0.0, -1.0, 1201.0, std::numeric_limits<double>::infinity(),
                         std::numeric_limits<double>::quiet_NaN()}) {
    Bitmap invalid;
    require(xpdf_render_page(doc.handle, 1, dpi, 0, &invalid.pixels,
                            &invalid.width, &invalid.height, &invalid.stride) == XPDF_INVALID_ARGUMENT,
            "reject invalid DPI");
    require(!invalid.pixels && invalid.width == 0 && invalid.height == 0 && invalid.stride == 0,
            "reset outputs after failure");
  }
  for (const auto dpi : {288.0, 1200.0}) {
    Bitmap oversized;
    require(xpdf_render_page(doc.handle, 3, dpi, 0, &oversized.pixels,
                            &oversized.width, &oversized.height, &oversized.stride) == XPDF_INVALID_ARGUMENT,
            "reject excessive pixel count or dimension before rendering");
    require(!oversized.pixels, "oversized render does not allocate output");
  }
  require(xpdf_get_page_size(doc.handle, 0, 0, &width, &height) == XPDF_INVALID_ARGUMENT,
          "reject page zero");
  require(width == 0 && height == 0, "reset failed dimensions");
  require(xpdf_get_page_size(doc.handle, 1, 45, &width, &height) == XPDF_INVALID_ARGUMENT,
          "reject unsupported rotation");
  require(xpdf_get_page_text(doc.handle, 4, &text) == XPDF_INVALID_ARGUMENT && !text,
          "reject text page beyond end");
  require(xpdf_get_page_size(doc.handle, 1, 0, nullptr, &height) == XPDF_INVALID_ARGUMENT,
          "reject missing dimension output");
  require(xpdf_get_page_count(nullptr) == 0 && *xpdf_get_last_error(), "reject null handle");
  const std::string originalError(xpdf_get_last_error());
  bool otherThreadPassed = false;
  std::thread worker([&]() {
    otherThreadPassed = xpdf_get_page_count(doc.handle) == 3
        && xpdf_get_last_error()[0] == '\0';
  });
  worker.join();
  require(otherThreadPassed && originalError == xpdf_get_last_error(), "thread-local errors");

  Document second;
  require(xpdf_open_document(fixture.path.c_str(), nullptr, &second.handle) == XPDF_SUCCESS,
          "open independent document");
  void *closed = doc.handle;
  xpdf_close_document(doc.handle);
  doc.handle = nullptr;
  require(xpdf_get_page_count(closed) == 0, "reject closed handle");
  require(xpdf_get_page_count(second.handle) == 3, "second document survives first close");

  void *failed = reinterpret_cast<void *>(1);
  require(xpdf_open_document("xpdf-winui-nonexistent.pdf", nullptr, &failed) == XPDF_ERROR
          && failed == nullptr, "missing file error and reset handle");
  require(xpdf_open_document(nullptr, nullptr, &failed) == XPDF_INVALID_ARGUMENT,
          "reject missing path");
  require(xpdf_open_document(fixture.path.c_str(), nullptr, nullptr) == XPDF_INVALID_ARGUMENT,
          "reject missing handle output");
#ifdef _WIN32
  require(xpdf_open_document("\xFF", nullptr, &failed) == XPDF_INVALID_ARGUMENT,
          "reject malformed UTF-8 path");
  Fixture encrypted("xpdf-winui-encrypted.pdf", makeEncryptedPdf());
  require(xpdf_open_document(encrypted.path.c_str(), nullptr, &failed) == XPDF_PASSWORD_REQUIRED
          && !failed, "encrypted PDF requires password");
  require(xpdf_open_document(encrypted.path.c_str(), "wrong", &failed) == XPDF_PASSWORD_REQUIRED
          && !failed, "encrypted PDF rejects wrong password");
  Document reader;
  require(xpdf_open_document(encrypted.path.c_str(), "reader", &reader.handle) == XPDF_SUCCESS,
          "open encrypted PDF with user password");
  require(xpdf_get_page_count(reader.handle) == 1, "encrypted PDF page count");
  require(xpdf_get_page_text(reader.handle, 1, &text) == XPDF_PERMISSION_DENIED && !text,
          "respect PDF copy restriction");
  Bitmap encryptedBitmap;
  require(xpdf_render_page(reader.handle, 1, 72, 0, &encryptedBitmap.pixels,
                          &encryptedBitmap.width, &encryptedBitmap.height,
                          &encryptedBitmap.stride) == XPDF_SUCCESS, "render copy-restricted PDF");
  Document owner;
  require(xpdf_open_document(encrypted.path.c_str(), "owner", &owner.handle) == XPDF_SUCCESS,
          "open encrypted PDF with owner password");
  require(xpdf_get_page_text(owner.handle, 1, &text) == XPDF_SUCCESS,
          "owner password grants PDF copy permission");
  xpdf_free_buffer(text);
#endif
  xpdf_free_buffer(nullptr);
  xpdf_close_document(nullptr);
}

} // namespace

int main() {
  try {
    runTests();
    std::cout << "Native WinUI bridge tests passed.\n";
    return 0;
  } catch (const std::exception &error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}
