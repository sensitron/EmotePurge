import { computed, effect, inject, Signal, signal, untracked, WritableSignal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, convertToParamMap, Params, Router } from '@angular/router';
import { debounceTime } from 'rxjs';

const PAGE_PARAM = 'page';

export interface ListQueryState<TParams extends Record<string, string>> {
  /** 1-based and always valid — a URL can carry `?page=abc` or `?page=-3`, a signal cannot. */
  readonly page: Signal<number>;
  /** The filter values, each falling back to its default when the URL does not carry it. */
  readonly params: Signal<TParams>;
  /** A real step in the reader's history — this is the one the back button exists for. */
  goToPage(page: number): void;
  /** Replaces the current history entry and returns to page 1. */
  setParams(patch: Partial<TParams>): void;
  /**
   * A text filter whose typing stays local and whose *settled* value goes into the URL.
   *
   * Both halves are necessary. Writing per keystroke would put a router navigation between the key
   * and the character on screen, and a value arriving back from the URL mid-word overwrites the
   * input and eats characters. Never writing would leave the URL holding a page number whose result
   * set nobody can reconstruct.
   *
   * Call it from a field initializer — it installs effects and therefore needs an injection context.
   */
  textFilter(key: keyof TParams & string, debounceMs: number): WritableSignal<string>;
}

/**
 * The URL as the source of truth for a paginated list: which page, which filters. Reload, deep link
 * and — the reason this exists — the browser's back button then all land where the reader left off,
 * instead of on page 1 of an unfiltered set.
 *
 * Five rules live here rather than in the five pages that use it, because each of them is the kind
 * that gets forgotten at the sixth page:
 *
 * - **Only a page change is a history step.** Filter changes navigate with `replaceUrl`, otherwise a
 *   filter that fires per keystroke would turn the back button into "undo one letter". The price is
 *   that back leaves the filtering in a single step rather than unwinding it — which is what the
 *   reset button is for.
 * - **A filter change returns to page 1.** The old page number belongs to the old result set, and a
 *   filter that shrinks the set below the current page would otherwise show a stray empty page.
 * - **`setParams` also clears the drafts of the keys it writes.** A value typed inside the debounce
 *   window has not reached the URL yet, so clearing only the URL leaves it to be written back one
 *   window later — under a filter combination the page just ruled out. That is not a caller's duty
 *   to remember: the two states are one state, and `setParams` is where they meet.
 * - **Neither navigation scrolls** (`scroll: 'manual'`, a per-navigation opt-out of the router's
 *   scroll restoration). Without it the router jumps to the top of the document on every keystroke,
 *   and on a page change it beats the pager to its own anchor — the router scrolls to `[0, 0]`,
 *   which ignores the `scroll-mt-*` that keeps the anchor clear of the sticky bars (§8.4/§8.5).
 * - **Default values are removed from the URL** instead of written as empty strings, so the common
 *   case reads `?page=3` and not `?page=3&action=&channel=&actor=`.
 *
 * Not covered: an out-of-range page (`?page=9999`) is passed through — the backend answers with an
 * empty page and the list shows its empty state. Clamping it would need `totalPages`, which only
 * exists after the response this state object is used to request.
 */
export function listQueryState<TParams extends Record<string, string> = Record<string, never>>(
  defaults: TParams = {} as TParams,
): ListQueryState<TParams> {
  const route = inject(ActivatedRoute);
  const router = inject(Router);

  // `queryParamMap` is BehaviorSubject-backed and emits synchronously, so the signal holds the real
  // params from the first read on; the initial value is there to not throw if it ever does not.
  const queryParams = toSignal(route.queryParamMap, { initialValue: convertToParamMap({}) });

  const page = computed(() => toPageNumber(queryParams().get(PAGE_PARAM)));

  const params = computed(() => {
    const map = queryParams();
    const resolved = { ...defaults };
    for (const key of Object.keys(defaults) as (keyof TParams & string)[]) {
      resolved[key] = (map.get(key) ?? defaults[key]) as TParams[typeof key];
    }
    return resolved;
  });

  const drafts = new Map<string, WritableSignal<string>>();

  const navigate = (next: Params, replaceUrl: boolean): void => {
    void router.navigate([], {
      relativeTo: route,
      queryParams: next,
      queryParamsHandling: 'merge',
      replaceUrl,
      scroll: 'manual',
    });
  };

  const setParams = (patch: Partial<TParams>): void => {
    const next: Params = { [PAGE_PARAM]: null };
    for (const [key, value] of Object.entries(patch)) {
      drafts.get(key)?.set(value as string);
      next[key] = value === defaults[key] ? null : value;
    }
    navigate(next, true);
  };

  const textFilter = (key: keyof TParams & string, debounceMs: number): WritableSignal<string> => {
    const fromUrl = computed(() => params()[key]);
    const draft = signal(untracked(fromUrl));
    drafts.set(key, draft);

    const settled = toSignal(toObservable(draft).pipe(debounceTime(debounceMs)), {
      initialValue: untracked(fromUrl),
    });

    // Draft -> URL, once the typing stops.
    effect(() => {
      const value = settled();
      if (value !== untracked(fromUrl)) {
        setParams({ [key]: value } as Partial<TParams>);
      }
    });

    // URL -> draft, but only when the URL moved without us: after our own write, and after any
    // `setParams` that cleared this key, the draft already equals it.
    effect(() => {
      const value = fromUrl();
      if (value !== untracked(draft)) {
        draft.set(value);
      }
    });

    return draft;
  };

  return {
    page,
    params,
    goToPage: (target) => navigate({ [PAGE_PARAM]: target <= 1 ? null : target }, false),
    setParams,
    textFilter,
  };
}

function toPageNumber(raw: string | null): number {
  const parsed = Number(raw);
  return Number.isInteger(parsed) && parsed >= 1 ? parsed : 1;
}
