import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, computed, effect, inject, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { FileImportResult, FileImportStep } from './file-import-step';
import { ForeignChannelImportResult, ForeignChannelStep } from './foreign-channel-step';

/**
 * Frozen at the moment of the triggering click (#91) — never a live signal, so a channel switch or
 * a set change while this dialog is open cannot retarget what a purge-run protocol is validated
 * against, nor where a foreign channel's emotes end up.
 */
export interface ImportSourceDialogData {
  channelName: string;
  setId: string;
}

/**
 * What the dialog closes with on success. The two file outcomes are unchanged (`FileImportResult`);
 * `'foreign'` is the third source. `undefined` on cancel, Escape or a backdrop click, same as every
 * other dialog in the app.
 */
export type ImportSourceDialogResult =
  FileImportResult | { kind: 'foreign'; picked: ForeignChannelImportResult };

/** Which sources the first step offers, in the order it offers them. A fourth source (7TV's
 *  leaderboard, #148) is one more entry here plus one more `@case` below — deliberately not a
 *  greyed-out placeholder in the meantime. */
type ImportSourceStep = 'file' | 'channel';

interface SourceOption {
  step: ImportSourceStep;
  labelKey: string;
  hintKey: string;
}

/** Added on top of `app-dialog-panel` for as long as the emote grid is on screen, removed again
 *  when it is not — see the rule of the same name in `styles.css`. */
const WIDE_PANEL_CLASS = 'app-dialog-panel-wide';

const SOURCE_OPTIONS: SourceOption[] = [
  {
    step: 'file',
    labelKey: 'import.source.file.label',
    hintKey: 'import.source.file.hint',
  },
  {
    step: 'channel',
    labelKey: 'import.source.channel.label',
    hintKey: 'import.source.channel.hint',
  },
];

/**
 * The **one** import dialog (#147). Its first step asks where the emotes come from; the branch the
 * user picks then continues inside the same dialog.
 *
 * Before this there were two header buttons — "Importieren" (file) and "Fremder Kanal" — which broke
 * the spec's E1 ("eine Quelle unter anderen im Import-Dialog, keine eigene Seite") by giving one
 * source a front door of its own. The source choice is now the dialog's first step, so every source
 * is reached the same way and a third one is a third row rather than a third button in the header.
 *
 * Neither branch opens a dialog of its own: the file step reports its parsed result, the channel
 * step reports its picked rows, and this dialog closes with whichever came back. The chains that
 * follow (`startRestoreFlow`, `startImportFlow`) are started by the trigger *after* this dialog has
 * closed, which is what keeps the app's one-dialog-at-a-time rule intact and lets each chain keep
 * its own ordering.
 *
 * **The pane is wide exactly while the emote grid is showing.** Width belongs to the pane and not to
 * the content (§7), so the switch is an overlay panel class and not a `max-w-*` somewhere in a
 * template — but it is *not* a property of the whole dialog either: all three form states (the source
 * choice, the file branch, the channel branch before a set has loaded) carry a form's worth of content
 * and looked lost in 72rem. They stay at the ordinary 28rem.
 *
 * That leaves exactly one resize, and it coincides with "Set laden" — i.e. with a content change the
 * reader is already watching, which is the one moment a size change reads as consequence rather than
 * caprice. Nothing else moves. The trigger is therefore the grid's visibility, not the step: entering
 * the channel branch changes nothing until the set is there.
 *
 * **The action row's cast follows the same state**, and only that one: "Weiter" is absent until the
 * grid is there. Without that, the channel step carried two forward actions at once — "Set laden" at
 * the field and a permanently disabled "Weiter" in the row below — which is what made a dialog around
 * one text input look busy.
 */
@Component({
  selector: 'app-import-source-dialog',
  imports: [Button, DialogShell, FileImportStep, ForeignChannelStep, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey() | transloco">
      @switch (step()) {
        @case ('choose') {
          <p id="import-source-legend" class="text-sm text-fg-secondary">
            {{ 'import.source.legend' | transloco }}
          </p>
          <!-- A menu of ruled rows, not a radiogroup: picking a source navigates, it does not
               parametrize a later action, so there is nothing left to confirm afterwards — the same
               reasoning that makes the file step's own "Datei auswählen" the whole action (§7.3). -->
          <div
            role="group"
            aria-labelledby="import-source-legend"
            class="flex flex-col border-t border-border"
          >
            @for (option of sourceOptions; track option.step) {
              <button
                type="button"
                class="flex min-h-11 flex-col items-start justify-center gap-0.5 border-b border-border px-2 py-3 text-left transition hover:bg-surface-inset"
                (click)="goTo(option.step)"
              >
                <span class="text-sm font-medium text-fg">{{ option.labelKey | transloco }}</span>
                <span class="text-xs text-fg-secondary">{{ option.hintKey | transloco }}</span>
              </button>
            }
          </div>
        }
        @case ('file') {
          <app-file-import-step
            [channelName]="data.channelName"
            [setId]="data.setId"
            (picked)="dialogRef.close($event)"
          />
        }
        @case ('channel') {
          <app-foreign-channel-step />
        }
      }

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close()"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      @if (step() !== 'choose') {
        <button dialog-actions type="button" appButton="neutral" buttonSize="lg" (click)="back()">
          {{ 'import.source.back' | transloco }}
        </button>
      }
      <!-- "Weiter" appears with the grid, not with the step. Before the set is loaded the channel
           branch already has a forward action — "Set laden", at the field it acts on — and a second,
           permanently disabled one beside it made a single text input look like it needed four
           buttons. Same condition as the pane width, deliberately: one state, one visible change.
           §7's "Abbrechen zuerst" is untouched by a trailing button that is sometimes absent. -->
      @if (gridVisible()) {
        <button
          dialog-actions
          type="button"
          appButton="primary"
          buttonSize="lg"
          [disabled]="channelResult() === null"
          (click)="continueWithChannel()"
        >
          {{ 'import.foreignChannel.continue' | transloco }}
        </button>
      }
    </app-dialog-shell>
  `,
})
export class ImportSourceDialog {
  protected readonly data = inject<ImportSourceDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ImportSourceDialogResult | undefined>>(DialogRef);

  private readonly channelStep = viewChild(ForeignChannelStep);

  protected readonly step = signal<'choose' | ImportSourceStep>('choose');
  protected readonly sourceOptions = SOURCE_OPTIONS;

  /** Each step names itself in the heading, so the dialog always says which branch you are in —
   *  the only orientation cue a one-dialog flow has besides "Zurück". */
  protected readonly titleKey = computed(() => {
    switch (this.step()) {
      case 'file':
        return 'restore.import.title';
      case 'channel':
        return 'import.foreignChannel.title';
      default:
        return 'import.source.title';
    }
  });

  protected readonly channelResult = computed(() => this.channelStep()?.result() ?? null);

  /** The one state this dialog changes shape around — see the class doc. `false` on every step that
   *  has no grid on it, including the channel step before its first successful load. It drives both
   *  the pane width and whether the action row carries a "Weiter" at all. */
  protected readonly gridVisible = computed(() => this.channelStep()?.showsGrid() ?? false);

  constructor() {
    // The overlay ref is CDK's own handle on the pane element, and the pane is where a width has to
    // land (§7). Nothing inside the template can reach it, and `panelClass` at open time cannot
    // either — it is decided before the dialog has any state.
    effect(() => {
      const overlayRef = this.dialogRef.overlayRef;
      if (this.gridVisible()) {
        overlayRef.addPanelClass(WIDE_PANEL_CLASS);
      } else {
        overlayRef.removePanelClass(WIDE_PANEL_CLASS);
      }
    });
  }

  protected goTo(step: ImportSourceStep): void {
    this.step.set(step);
  }

  protected back(): void {
    this.step.set('choose');
  }

  protected continueWithChannel(): void {
    const picked = this.channelResult();
    if (picked === null) {
      return;
    }
    this.dialogRef.close({ kind: 'foreign', picked });
  }
}

export function openImportSourceDialog(
  dialog: Dialog,
  data: ImportSourceDialogData,
): DialogRef<ImportSourceDialogResult | undefined> {
  return openAppDialog<ImportSourceDialogResult | undefined, ImportSourceDialogData>(
    dialog,
    ImportSourceDialog,
    { data },
  );
}
