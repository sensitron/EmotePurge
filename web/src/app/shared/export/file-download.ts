/**
 * Client-side file downloads for the export features (A6/A12). There is no server-side export
 * endpoint on purpose: the serialized data is the already-loaded read model, so a download must
 * not be able to see more than the page does.
 */

/**
 * Keeps a filename part portable: lowercase, only `[a-z0-9_.-]`, everything else collapsed into a
 * single `-`, capped at 80 characters. Free text (channel casing, titles) never reaches a filename
 * unsanitized.
 */
export function sanitizeFilenamePart(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9_.-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80);
}

/** Builds a Blob, clicks a synthetic `<a download>`, and frees the object URL again. */
export function downloadFile(filename: string, content: string, mimeType: string): void {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    // Never appended to the DOM — click() works on a detached anchor in every supported browser,
    // and not appending means nothing to clean up when click() throws.
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}
