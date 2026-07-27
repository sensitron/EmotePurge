import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-emote-card-header',
  template: `
    <div class="flex h-4 w-full items-center gap-1">
      @if (showCheckbox()) {
        <input
          type="checkbox"
          class="h-3.5 w-3.5 shrink-0"
          tabindex="-1"
          [checked]="checked()"
          (click)="checkboxClick.emit($event)"
        />
      }
      <span class="min-w-0 flex-1 truncate text-left text-xs" [attr.title]="name()">{{ name() }}</span>
    </div>
  `,
})
export class EmoteCardHeader {
  readonly name = input.required<string>();
  readonly checked = input(false);
  readonly showCheckbox = input(true);
  readonly checkboxClick = output<MouseEvent>();
}
