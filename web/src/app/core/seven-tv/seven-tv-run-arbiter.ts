import { Service, Signal, computed, inject } from '@angular/core';

import { SevenTvDeleteService } from './seven-tv-delete.service';
import { SevenTvRestoreService } from './seven-tv-restore.service';

/** 'import' is reserved for K3 (#72, `SevenTvImportService`) — not built yet, see the class doc. */
export type SevenTvRunKind = 'delete' | 'restore' | 'import';

/**
 * Answers exactly one question: is a 7TV run active right now, and which kind? It exists because
 * delete and restore (and, from K3 on, import) each run over their *own* `SevenTvRunEngine`
 * instance (see the note on that in `SevenTvDeleteService`/`SevenTvRestoreService`), so no single
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
 * The check order below (`delete` before `restore`, `import` last once it exists) is a display
 * choice, not an exclusion rule — by construction at most one of the three can be running at a
 * time, because every start site checks `activeRun() === null` first (Task 4). See the arbiter spec
 * for the constructed case (two engines forced active at once) that pins the order anyway.
 */
@Service()
export class SevenTvRunArbiter {
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);

  readonly activeRun: Signal<SevenTvRunKind | null> = computed(() => {
    if (this.deleteService.isRunning()) {
      return 'delete';
    }
    if (this.restoreService.isRunning()) {
      return 'restore';
    }
    return null;
  });
}
