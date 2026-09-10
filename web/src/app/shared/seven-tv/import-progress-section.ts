import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { RunProgressPanel } from './run-progress-panel';

/**
 * The usage-stats page's window onto a copy run (#72, K3) — a page-level section, not content
 * projected into `app-mass-delete-panel`: that panel is also mounted on the voting-results page,
 * where a run copying emotes into some *other* channel's set has no business appearing.
 *
 * No inputs, on purpose: the run lives in `SevenTvImportService` (`providedIn: 'root'`), so this
 * component shows the same run regardless of which channel's usage-stats page happens to mount
 * it — including a channel that is neither the run's source nor its target (R9). That is exactly
 * why the target line below is unconditional rather than folded into some "your import" framing:
 * on a page that is not the target, an unlabelled progress bar would read as a run *of* that page.
 *
 * Renders nothing until there is something to show (`isRunning()` or a non-empty `queue()`) — the
 * same "settled run stays visible" contract the mass-delete panel already has, so the post-run
 * summary and the resync notice remain readable after the last row finishes.
 */
@Component({
  selector: 'app-import-progress-section',
  imports: [Button, NoticeBanner, RouterLink, RunProgressPanel, TranslocoPipe],
  template: `
    <!-- #149 P2 (independent review): gated on duplicateNoticePending, not just skippedDuplicates() >
         0 — a transient notice (design doc §4.5), not a persistent one, so it never sits attached to
         a *later*, unrelated run's details with nothing to clear it. See that signal's doc for why
         it also has to be what keeps the dock (and this section) mounted for a fully-refused
         (all-duplicates) run, which leaves no run/queue behind of its own. -->
    @if (importService.duplicateNoticePending() && importService.skippedDuplicates() > 0) {
      <p class="text-sm text-fg-secondary" role="status">
        {{ skippedDuplicatesKey() | transloco: { count: importService.skippedDuplicates() } }}
      </p>
    }
    <!-- The fresh pre-send duplicate check's fetch failed (already-present-filter.ts) — every row
         still went through, so a duplicate may have slipped in undetected. A quiet notice, not an
         alarm: the run is still expected to succeed, this only says the guard could not run. -->
    @if (importService.duplicateNoticePending() && !importService.duplicateCheckAvailable()) {
      <p class="text-sm text-fg-secondary" role="status">
        {{ 'import.duplicateCheckUnavailable' | transloco }}
      </p>
    }
    @if (importService.isRunning() || importService.queue().length > 0) {
      @if (importService.run(); as run) {
        <div class="flex flex-col gap-2">
          <p class="text-xs text-fg-muted">
            {{ 'import.summary.target' | transloco: { channel: run.targetChannelName } }}
          </p>
          <app-run-progress-panel
            [items]="importService.queue()"
            [isRunning]="importService.isRunning()"
            labelPrefix="import"
            [syncReport]="importService.syncReport()"
            [rateLimitPauseSeconds]="importService.rateLimitPauseSeconds()"
            (cancelled)="importService.cancel()"
            (dismissed)="importService.reset()"
            (syncRetryRequested)="importService.retrySyncReport()"
          >
            <ng-container run-actions>
              @if (importService.abortedForPrivileges()) {
                <app-notice-banner variant="warning">
                  {{ 'import.summary.insufficientPrivileges' | transloco }}
                </app-notice-banner>
              }
              @if (resyncNoticeKey(); as noticeKey) {
                <span class="text-xs text-fg-muted" role="status">{{ noticeKey | transloco }}</span>
              }
              <a
                appButton="outline"
                [routerLink]="['/channels', run.targetChannelName, 'usage-stats']"
              >
                {{ 'import.summary.openTarget' | transloco }}
              </a>
            </ng-container>
          </app-run-progress-panel>
        </div>
      }
    }
  `,
})
export class ImportProgressSection {
  protected readonly importService = inject(SevenTvImportService);

  /** #149/T5: wording for how many rows the fresh pre-run duplicate check
   *  (`already-present-filter.ts`, run from `import-flow.ts`) dropped — shown independently of the
   *  run-progress panel below, because a run where the fresh check caught everything queues nothing
   *  and would otherwise leave that panel hidden (its own gate is `isRunning() || queue().length
   *  > 0`), silently swallowing the one thing the user needs to see in that case. */
  protected readonly skippedDuplicatesKey = computed(() =>
    pluralKey(this.importService.skippedDuplicates(), 'import.skippedDuplicates'),
  );

  /** Same pattern as `MassDeletePanel.resyncNoticeKey` — only the key family differs. */
  protected readonly resyncNoticeKey = computed(() => {
    switch (this.importService.resyncTrigger()) {
      case 'pending':
        return 'import.resync.pending';
      case 'succeeded':
        return 'import.resync.succeeded';
      case 'cooldown':
        return 'import.resync.cooldown';
      case 'failed':
        return 'import.resync.failed';
      default:
        return null;
    }
  });
}
