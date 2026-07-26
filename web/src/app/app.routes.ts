import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
import { channelManagementGuard } from './core/channels/channel-management.guard';
import { LoginPage } from './features/login/login-page';

export const routes: Routes = [
  { path: 'login', component: LoginPage },
  {
    path: '',
    loadComponent: () => import('./features/shell/app-shell').then((m) => m.AppShell),
    children: [
      {
        path: '',
        loadComponent: () => import('./features/overview/overview-page').then((m) => m.OverviewPage),
        canActivate: [authGuard],
      },
      {
        path: 'channels/:channelName',
        loadComponent: () =>
          import('./features/channel-workspace/channel-workspace-layout').then((m) => m.ChannelWorkspaceLayout),
        children: [
          { path: '', redirectTo: 'vote-sessions', pathMatch: 'full' },
          {
            // Management tool — must actually be allowed to manage THIS channel, not just be
            // logged in (channelManagementGuard), unlike the vote-session pages below.
            path: 'usage-stats',
            loadComponent: () => import('./features/usage-stats/usage-stats-page').then((m) => m.UsageStatsPage),
            canActivate: [channelManagementGuard],
          },
          {
            // Deliberately no authGuard — vote-session pages must be viewable (and shareable)
            // by anonymous visitors; login is triggered from inside the page (the vote button),
            // not by a route redirect.
            path: 'vote-sessions',
            loadComponent: () =>
              import('./features/voting/vote-session-list-page').then((m) => m.VoteSessionListPage),
          },
          {
            path: 'vote-sessions/:sessionId',
            loadComponent: () =>
              import('./features/voting/vote-session-detail-page').then((m) => m.VoteSessionDetailPage),
          },
        ],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
