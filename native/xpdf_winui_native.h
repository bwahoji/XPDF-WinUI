#ifndef XPDF_WINUI_NATIVE_H
#define XPDF_WINUI_NATIVE_H

#if defined(_WIN32)
#  if defined(XPDF_WINUI_NATIVE_EXPORTS)
#    define XPDF_API __declspec(dllexport)
#  else
#    define XPDF_API __declspec(dllimport)
#  endif
#  define XPDF_CALL __cdecl
#else
#  define XPDF_API __attribute__((visibility("default")))
#  define XPDF_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum XpdfStatus {
  XPDF_SUCCESS = 0,
  XPDF_ERROR = 1,
  XPDF_PASSWORD_REQUIRED = 2,
  XPDF_INVALID_ARGUMENT = 3,
  XPDF_PERMISSION_DENIED = 4
};

// Handles must be closed once; pixel and text buffers must be freed here,
// even when the caller uses a different C runtime. All operations are serialized.
XPDF_API int XPDF_CALL xpdf_open_document(const char *utf8Path,
                                         const char *utf8Password,
                                         void **document);
XPDF_API void XPDF_CALL xpdf_close_document(void *document);
// Returns zero on failure; consult xpdf_get_last_error().
XPDF_API int XPDF_CALL xpdf_get_page_count(void *document);

// Page numbers are one-based. Rotation is an additional clockwise rotation
// of 0, 90, 180 or 270 degrees; the PDF's intrinsic rotation is included.
// Sizes describe the visible CropBox, in points (1/72 inch).
XPDF_API int XPDF_CALL xpdf_get_page_size(void *document, int page,
                                         int rotation, double *widthPoints,
                                         double *heightPoints);

// Fills widthPoints/heightPoints with the CropBox sizes for pages 1..count.
// count must be in [1, pageCount]. One native call batches the whole
// document so the managed layer does not round-trip per page.
XPDF_API int XPDF_CALL xpdf_get_page_sizes(void *document, int rotation,
                                           double *widthPoints,
                                           double *heightPoints, int count);

// Returns opaque, top-down BGRA8 pixels with stride == width * 4.
// DPI must be in [1, 1200]; output is limited to 16384 pixels per axis
// and 32 megapixels total. On failure all supplied outputs are reset.
XPDF_API int XPDF_CALL xpdf_render_page(void *document, int page, double dpi,
                                       int rotation, unsigned char **pixels,
                                       int *width, int *height, int *stride);

// Renders one rectangular tile from a page. sliceX/sliceY are the top-left
// corner in the full rendered page's device-pixel coordinate system, and
// sliceWidth/sliceHeight are the tile dimensions in pixels. The full page is
// measured at the requested DPI and rotation (including the PDF's intrinsic
// rotation); the tile must lie completely inside it. Output has the same
// top-down BGRA8 layout and limits as xpdf_render_page.
XPDF_API int XPDF_CALL xpdf_render_page_tile(
    void *document, int page, double dpi, int rotation, int sliceX, int sliceY,
    int sliceWidth, int sliceHeight, unsigned char **pixels, int *width,
    int *height, int *stride);
XPDF_API void XPDF_CALL xpdf_free_buffer(void *buffer);

// Returns NUL-terminated UTF-8 outline lines, one per bookmark, of the form
// "<depth>\x1F<page>\x1F<title>\n" (page is 0 when the item does not target a
// page in this file). An empty document outline yields an empty string.
// Free with xpdf_free_buffer.
XPDF_API int XPDF_CALL xpdf_get_outline(void *document, char **utf8Lines);

// Returns NUL-terminated UTF-8 in reading order, with LF line endings.
// PDF copy permissions are respected. Image-only pages return empty text.
XPDF_API int XPDF_CALL xpdf_get_page_text(void *document, int page,
                                         char **utf8Text);

// Thread-local storage, valid until the next operation on the calling thread.
// Closing documents and freeing buffers preserve the previous error on success.
XPDF_API const char *XPDF_CALL xpdf_get_last_error(void);

#ifdef __cplusplus
}
#endif

#endif
