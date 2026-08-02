import { MockInstance, afterEach, describe, expect, it, vi } from 'vitest';

import { downloadFile, sanitizeFilenamePart } from './file-download';

describe('sanitizeFilenamePart', () => {
  it('lowercases and keeps the allowed characters', () => {
    expect(sanitizeFilenamePart('HandOfBlood')).toBe('handofblood');
    expect(sanitizeFilenamePart('a_b.c-d')).toBe('a_b.c-d');
  });

  it('collapses runs of disallowed characters into a single dash', () => {
    expect(sanitizeFilenamePart('a / b "c"')).toBe('a-b-c');
  });

  it('trims leading and trailing dashes', () => {
    expect(sanitizeFilenamePart('!!weird!!')).toBe('weird');
  });

  it('caps the result at 80 characters', () => {
    expect(sanitizeFilenamePart('x'.repeat(120))).toHaveLength(80);
  });
});

describe('downloadFile', () => {
  afterEach(() => {
    // Spies only — never replace the global URL object, other suites construct real URLs.
    vi.restoreAllMocks();
  });

  /** jsdom may not implement the object-URL pair; give spyOn something to wrap if so. */
  function stubObjectUrls(): { create: MockInstance; revoke: MockInstance } {
    if (!('createObjectURL' in URL)) {
      Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
    }
    return {
      create: vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:test'),
      revoke: vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined),
    };
  }

  it('clicks a synthetic anchor with the filename and frees the object URL', () => {
    const { create, revoke } = stubObjectUrls();

    let anchor: HTMLAnchorElement | undefined;
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
      const element = originalCreateElement(tag);
      if (tag === 'a') {
        anchor = element as HTMLAnchorElement;
        vi.spyOn(element as HTMLAnchorElement, 'click').mockImplementation(() => undefined);
      }
      return element;
    });

    downloadFile('export.csv', 'a,b', 'text/csv;charset=utf-8');

    expect(create).toHaveBeenCalledOnce();
    const blob = create.mock.calls[0][0] as Blob;
    expect(blob.type).toBe('text/csv;charset=utf-8');
    expect(anchor?.download).toBe('export.csv');
    expect(anchor?.click).toHaveBeenCalledOnce();
    expect(revoke).toHaveBeenCalledWith('blob:test');
  });

  it('frees the object URL even when the click throws', () => {
    const { revoke } = stubObjectUrls();
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
      const element = originalCreateElement(tag);
      if (tag === 'a') {
        vi.spyOn(element as HTMLAnchorElement, 'click').mockImplementation(() => {
          throw new Error('blocked');
        });
      }
      return element;
    });

    expect(() => downloadFile('x.json', '{}', 'application/json')).toThrow('blocked');
    expect(revoke).toHaveBeenCalledWith('blob:test');
  });
});
