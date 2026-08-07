import { DestroyRef, Injectable, inject, signal } from '@angular/core';

/**
 * The primary pointing device, not any attached one. `any-pointer: coarse` is true for a desktop
 * with a touchscreen plugged in — a machine that still has DevTools and therefore still has a way
 * to obtain the 7TV write token, so it must keep the delete engine.
 */
const COARSE_POINTER_QUERY = '(pointer: coarse)';

/**
 * Whether the app is being pointed at with a finger.
 *
 * The single place in the frontend that touches `matchMedia`. It gates capability, not layout:
 * width decides what fits (the sidecar from `lg` up), the pointer decides what can be operated at
 * all. A half-width desktop window has hover, the group-hover trigger and precise clicks — nothing
 * is broken there — while a phone has no hover, no 44 px-safe 20 px target, and no DevTools to read
 * the 7TV token out of local storage.
 *
 * For purely visual hiding prefer Tailwind's `pointer-coarse:` variant; this signal is for the
 * cases where a handler, a service call or an ARIA attribute has to disappear, which CSS cannot do.
 */
@Injectable({ providedIn: 'root' })
export class PointerModeService {
  private readonly destroyRef = inject(DestroyRef);

  private readonly coarse = signal(false);

  readonly isCoarse = this.coarse.asReadonly();

  constructor() {
    const query = matchMedia(COARSE_POINTER_QUERY);
    this.coarse.set(query.matches);

    const onChange = (event: MediaQueryListEvent) => this.coarse.set(event.matches);
    query.addEventListener('change', onChange);
    this.destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
  }
}
