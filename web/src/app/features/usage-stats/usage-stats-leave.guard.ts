import { Dialog } from '@angular/cdk/dialog';
import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanDeactivateFn, RouterStateSnapshot } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { map, Observable, of } from 'rxjs';

import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { ConfirmDialogData, openConfirmDialog } from '../../shared/ui/confirm-dialog';

/**
 * Asks before leaving the usage-stats page while an import run (K3, #72) is still going. The run
 * itself lives in `SevenTvImportService` (`providedIn: 'root'`), so it keeps going in the
 * background regardless of the answer here — this guard never cancels or resets anything, it only
 * asks. Fires only while `isRunning()` is true; every other navigation away from the page passes
 * through immediately, with no dialog and no delay (R11).
 *
 * A pure channel switch (`/channels/a/usage-stats` -> `/channels/b/usage-stats`) is exempt even
 * while a run is active: `paramsInheritanceStrategy: 'always'` plus the default
 * `runGuardsAndResolvers: 'paramsChange'` make Angular run this guard on that navigation too, even
 * though the component is reused and the same run keeps showing in its dock on the new channel's
 * page — there is nothing there for the user to lose, so asking would warn about nothing. Detected
 * by comparing the *route*, not the channel name: `leadsToSameRoute` walks the next router state
 * looking for a snapshot whose `routeConfig` is the same object as the current route's — that
 * reference is stable per route definition regardless of which segment holds the channel name, so
 * this makes no assumption about the concrete path string.
 *
 * `closed` resolves to `undefined` on Escape/backdrop dismissal, same as every other confirm
 * dialog in the app — only an explicit `true` lets the navigation through.
 */
export const usageStatsLeaveGuard: CanDeactivateFn<unknown> = (
  _component,
  currentRoute,
  _currentState,
  nextState,
): Observable<boolean> => {
  const importService = inject(SevenTvImportService);

  if (!importService.isRunning()) {
    return of(true);
  }

  if (leadsToSameRoute(currentRoute, nextState)) {
    return of(true);
  }

  const dialog = inject(Dialog);
  const translocoService = inject(TranslocoService);

  const data: ConfirmDialogData = {
    message: translocoService.translate('import.leaveWhileRunning.message'),
    confirmLabel: translocoService.translate('import.leaveWhileRunning.confirm'),
  };

  return openConfirmDialog(dialog, data).closed.pipe(map((confirmed) => confirmed === true));
};

/** Whether the navigation described by `nextState` lands, anywhere in its route tree, on the same
 *  route definition as `currentRoute` — i.e. whether it is a reuse of this very page rather than a
 *  move to a different one. */
function leadsToSameRoute(
  currentRoute: ActivatedRouteSnapshot,
  nextState: RouterStateSnapshot,
): boolean {
  let candidate: ActivatedRouteSnapshot | null = nextState.root;
  while (candidate !== null) {
    if (candidate.routeConfig === currentRoute.routeConfig) {
      return true;
    }
    candidate = candidate.firstChild;
  }
  return false;
}
