import { Dialog } from '@angular/cdk/dialog';
import { Component, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';
import { startForeignChannelImportFlow } from './foreign-import-flow';

/**
 * The header button for the third import source (spec §7, E1): a channel EmotePurge does not track,
 * read live from 7TV. One source among the others, named plainly and not advertised — no catalogue,
 * no own page.
 *
 * Takes no inputs at all, unlike `FileImportTrigger`: nothing about this chain refers to the page it
 * is placed on. The source is typed into the picker, and the target is chosen from `listMine()`
 * inside `ImportTargetDialog` — so there is no page state to freeze at the click and no channel
 * switch that could retarget anything mid-flow.
 *
 * Injects its own services, same reason as its neighbour: the host page gets no method of its own.
 * Locked only while some 7TV run is active — the arbiter's rule for every run-starting control; the
 * running progress in the dock is the reason, so the button states none of its own (§4.2).
 */
@Component({
  selector: 'app-foreign-channel-import-trigger',
  imports: [Button, TranslocoPipe],
  template: `
    <button
      type="button"
      appButton="neutral"
      class="disabled:cursor-not-allowed"
      [disabled]="disabled()"
      (click)="openDialog()"
    >
      {{ 'import.foreignChannel.trigger' | transloco }}
    </button>
  `,
})
export class ForeignChannelImportTrigger {
  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly dialog = inject(Dialog);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly importService = inject(SevenTvImportService);

  protected readonly disabled = computed(() => this.arbiter.activeRun() !== null);

  protected openDialog(): void {
    startForeignChannelImportFlow({
      dialog: this.dialog,
      emoteAdminService: this.emoteAdminService,
      tokenService: this.tokenService,
      importService: this.importService,
      arbiter: this.arbiter,
    });
  }
}
