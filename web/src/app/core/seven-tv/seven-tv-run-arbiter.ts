import { Service, Signal, computed, inject } from '@angular/core';

import { SevenTvDeleteService } from './seven-tv-delete.service';
import { SevenTvImportService } from './seven-tv-import.service';
import { SevenTvRestoreService } from './seven-tv-restore.service';

/** The three 7TV-writing runs, one engine instance each — see the class doc. */
export type SevenTvRunKind = 'delete' | 'restore' | 'import';

/**
 * Answers exactly one question: is a 7TV run active right now, and which kind? It exists because
 * delete, restore and import each run over their *own* `SevenTvRunEngine` instance (see the note on
 * that in `SevenTvDeleteService`/`SevenTvRestoreService`/`SevenTvImportService`), so no single
 * engine's `isRunning` can speak for all three — a panel that wants to disable every 7TV-writing
 * button while *any* of them runs needs one place that looks across all of them.
 *
 * `activeRun` is a `computed`, not a held lock: there is no `tryAcquire`/`release` here, on
 * purpose. The issue that originally proposed this class asked for hand-kept state; the plan for
 * #70 (Task 3, R1 in docs/DECISIONS.md) replaced that with derivation after tracing a stuck-lock
 * path that hand-kept state cannot avoid — `tryAcquire` winning right before `engine.start` refuses
 * the run (e.g. a 401 in the *previous* run already cleared the write token) would leave the lock
 * held with nothing running, wedging every 7TV-writing button app-wide until a reload. Deriving
 * instead of locking makes that path structurally impossible: `SevenTvRunEngine.start` sets its
 * `isRunning` signal synchronously, and only *after* every rejection reason (already running, empty
 * queue, missing token) has already returned `false` — so a rejected start leaves no trace to read
 * here. `finish()` is the engine's only way back to `isRunning() === false`, reached from the
 * normal end, from `cancel()`, and from the RxJS error path alike. A second, hand-kept flag next to
 * that could only ever drift from it; reading the two services' own `isRunning` signals cannot.
 *
 * None of the three services knows this class, and none of them reports its start or end here —
 * that is what keeps the dependency edges one-way (arbiter → services) and free of a DI cycle, and
 * it is why adding the import in #72 was a single branch below rather than a call site in the
 * import service (R1 there).
 *
 * The check order below (`delete`, then `restore`, then `import`) is a display
 * choice, not an exclusion rule — by construction at most one of the three can be running at a
 * time, because every start site checks `activeRun() === null` first (Task 4). See the arbiter spec
 * for the constructed case (two engines forced active at once) that pins the order anyway.
 */
@Service()
export class SevenTvRunArbiter {
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly importService = inject(SevenTvImportService);

  readonly activeRun: Signal<SevenTvRunKind | null> = computed(() => {
    if (this.deleteService.isRunning()) {
      return 'delete';
    }
    if (this.restoreService.isRunning()) {
      return 'restore';
    }
    if (this.importService.isRunning()) {
      return 'import';
    }
    return null;
  });
}
