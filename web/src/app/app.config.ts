import {
  ApplicationConfig,
  inject,
  isDevMode,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import {
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
  withRouterConfig,
} from '@angular/router';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideTransloco } from '@jsverse/transloco';

import { routes } from './app.routes';
import { apiAuthInterceptor } from './core/http/api-auth.interceptor';
import { LanguageService, resolveInitialLang, SUPPORTED_LANGS } from './core/i18n/language.service';
import { TranslocoHttpLoader } from './transloco-loader';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      withComponentInputBinding(),
      // Default 'emptyOnly' strategy would stop `channelName` (owned by the ':channelName'
      // segment) from reaching non-empty-path children like 'usage-stats' via input binding.
      // onSameUrlNavigation 'reload': clicking an anchor link for the fragment the URL already
      // carries must still scroll (anchorScrolling only acts on completed navigations).
      withRouterConfig({ paramsInheritanceStrategy: 'always', onSameUrlNavigation: 'reload' }),
      // The router intercepts even plain `href="#features"` clicks as fragment navigations and,
      // without this, neither scrolls itself nor lets the browser's native jump through — the
      // landing page's anchor nav did nothing. scrollPositionRestoration also puts route changes
      // back at the top instead of keeping the previous page's scroll offset.
      withInMemoryScrolling({ anchorScrolling: 'enabled', scrollPositionRestoration: 'enabled' }),
    ),
    provideHttpClient(withFetch(), withInterceptors([apiAuthInterceptor])),
    provideTransloco({
      config: {
        availableLangs: [...SUPPORTED_LANGS],
        // Matches LanguageService's own resolution so the correct translation file loads on the
        // very first HTTP request instead of fetching the config default and re-fetching after.
        defaultLang: resolveInitialLang(),
        reRenderOnLangChange: true,
        prodMode: !isDevMode(),
      },
      loader: TranslocoHttpLoader,
    }),
    // Eagerly instantiate LanguageService so `<html lang>` is corrected before first paint even on
    // routes whose first-rendered component doesn't itself inject it (e.g. no LanguageSwitcher
    // mounted yet in that subtree).
    provideAppInitializer(() => {
      inject(LanguageService);
    }),
  ],
};
