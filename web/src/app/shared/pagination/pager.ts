import { Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'app-pager',
  imports: [TranslocoPipe],
  template: `
    @if (totalPages() > 1) {
      <div class="flex items-center justify-between text-sm text-slate-400">
        <button
          type="button"
          [disabled]="page() <= 1"
          (click)="pageChange.emit(page() - 1)"
          class="rounded-md border border-slate-700 px-3 py-1.5 transition hover:bg-slate-800 disabled:opacity-40 disabled:hover:bg-transparent"
        >
          {{ 'pager.previous' | transloco }}
        </button>
        <span>{{ 'pager.pageOf' | transloco: { page: page(), totalPages: totalPages() } }}</span>
        <button
          type="button"
          [disabled]="page() >= totalPages()"
          (click)="pageChange.emit(page() + 1)"
          class="rounded-md border border-slate-700 px-3 py-1.5 transition hover:bg-slate-800 disabled:opacity-40 disabled:hover:bg-transparent"
        >
          {{ 'pager.next' | transloco }}
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
