import { Component, input } from '@angular/core';

@Component({
  selector: 'app-emote-card-header',
  // h-5 is part of the card-height contract — ROW_HEIGHT_PX in both grid pages budgets for it.
  template: `
    <span class="block h-5 w-full truncate text-left text-xs" [attr.title]="name()">{{
      name()
    }}</span>
  `,
})
export class EmoteCardHeader {
  readonly name = input.required<string>();
}
