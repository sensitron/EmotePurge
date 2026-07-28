import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-pager',
  template: `
    @if (totalPages() > 1) {
      <div class="flex items-center justify-between text-sm text-slate-400">
        <button
          type="button"
          [disabled]="page() <= 1"
          (click)="pageChange.emit(page() - 1)"
          class="rounded-md border border-slate-700 px-3 py-1.5 transition hover:bg-slate-800 disabled:opacity-40 disabled:hover:bg-transparent"
        >
          Zurück
        </button>
        <span>Seite {{ page() }} von {{ totalPages() }}</span>
        <button
          type="button"
          [disabled]="page() >= totalPages()"
          (click)="pageChange.emit(page() + 1)"
          class="rounded-md border border-slate-700 px-3 py-1.5 transition hover:bg-slate-800 disabled:opacity-40 disabled:hover:bg-transparent"
        >
          Weiter
        </button>
      </div>
    }
  `,
})
export class Pager {
  readonly page = input.required<number>();
  readonly totalPages = input.required<number>();
  readonly pageChange = output<number>();
}
