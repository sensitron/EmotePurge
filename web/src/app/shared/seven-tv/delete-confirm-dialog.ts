import { Component, computed, input, output } from '@angular/core';

@Component({
  selector: 'app-delete-confirm-dialog',
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 px-4" role="dialog" aria-modal="true">
      <div class="w-full max-w-md rounded-lg bg-slate-900 p-6 shadow-xl">
        <h2 class="mb-3 text-lg font-medium">{{ emotes().length }} Emotes von 7TV löschen?</h2>
        <ul class="mb-4 max-h-48 space-y-1 overflow-y-auto text-sm text-slate-300">
          @for (emote of previewEmotes(); track emote) {
            <li>{{ emote }}</li>
          }
          @if (emotes().length > previewEmotes().length) {
            <li class="text-slate-500">…und {{ emotes().length - previewEmotes().length }} weitere</li>
          }
        </ul>
        <p class="mb-4 text-sm text-amber-400">
          Das kann nicht rückgängig gemacht werden. Löschen läuft danach automatisch nacheinander
          mit kurzer Verzögerung zwischen den Emotes.
        </p>
        <div class="flex justify-end gap-2">
          <button
            type="button"
            class="rounded-md border border-slate-700 px-4 py-2 text-sm text-slate-300 transition hover:bg-slate-800"
            (click)="cancelled.emit()"
          >
            Abbrechen
          </button>
          <button
            type="button"
            class="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-red-500"
            (click)="confirmed.emit()"
          >
            Löschen starten
          </button>
        </div>
      </div>
    </div>
  `,
})
export class DeleteConfirmDialog {
  readonly emotes = input.required<string[]>();
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  protected readonly previewEmotes = computed(() => this.emotes().slice(0, 50));
}
