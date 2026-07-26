import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { provideHttpClient, withFetch } from '@angular/common/http';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      withComponentInputBinding(),
      // Default 'emptyOnly' strategy would stop `channelName` (owned by the ':channelName'
      // segment) from reaching non-empty-path children like 'usage-stats' via input binding.
      withRouterConfig({ paramsInheritanceStrategy: 'always' }),
    ),
    provideHttpClient(withFetch())
  ]
};
