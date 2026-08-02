import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { ThemeService } from './core/theme/theme.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `<router-outlet />`,
})
export class App {
  // Injected here and nowhere else on purpose: this is the only component rendered on *every*
  // route. The theme menu itself lives in the app shell, but the landing page and the login page
  // render outside it — and a root-provided service is not constructed until somebody asks for it.
  // Without this line a visitor on either of those pages keeps whatever public/theme-init.js
  // stamped at load and does not follow a live OS theme change, because the service that listens
  // for it was never created.
  private readonly themeService = inject(ThemeService);
}
