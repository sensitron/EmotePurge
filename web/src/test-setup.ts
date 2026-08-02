/**
 * Global Vitest setup. Runs before every spec file.
 *
 * jsdom does not implement `window.matchMedia` at all — it is a documented gap, not something the
 * app can encounter in a browser. Anything reaching a media query (ThemeService, and therefore the
 * root App component, and therefore any spec that instantiates it) would otherwise fail with
 * "matchMedia is not a function" for a reason that has nothing to do with what the spec is testing.
 *
 * Deliberately the dumbest possible stand-in: it never matches and never fires. Specs that care
 * about media-query behaviour install their own richer fake (see core/theme/theme.service.spec.ts),
 * and `vi.stubGlobal` there takes precedence over this.
 */
if (typeof window !== 'undefined' && typeof window.matchMedia !== 'function') {
  window.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}
