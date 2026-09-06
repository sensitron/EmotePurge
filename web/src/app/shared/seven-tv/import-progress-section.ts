import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

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
