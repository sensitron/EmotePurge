import { Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from '../ui/button';

@Component({
  selector: 'app-pager',
  imports: [Button, TranslocoPipe],
  template: `
    @if (totalPages() > 1) {
      <div class="flex items-center justify-between text-sm text-slate-400">
        <button
          type="button"
          appButton="outline"
          class="disabled:hover:bg-transparent"
          [disabled]="page() <= 1"
          (click)="pageChange.emit(page() - 1)"
        >
          {{ 'pager.previous' | transloco }}
        </button>
        <span>{{ 'pager.pageOf' | transloco: { page: page(), totalPages: totalPages() } }}</span>
        <button
          type="button"
          appButton="outline"
          class="disabled:hover:bg-transparent"
          [disabled]="page() >= totalPages()"
          (click)="pageChange.emit(page() + 1)"
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
