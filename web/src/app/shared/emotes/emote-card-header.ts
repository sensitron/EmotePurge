import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-emote-card-header',
  template: `
    <div class="flex h-5 w-full items-center gap-1">
      @if (showCheckbox()) {
        <!-- Small by design: the whole card is the >=24px selection target (WCAG 2.5.8's
             equivalent-target exception); the checkbox only mirrors the state visually. -->
        <input
          type="checkbox"
          class="h-4 w-4 shrink-0 accent-purple-600"
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
