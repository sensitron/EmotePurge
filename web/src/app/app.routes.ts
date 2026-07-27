import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
import { channelManagementGuard } from './core/channels/channel-management.guard';
import { voteSessionAccessGuard } from './core/voting/vote-session-access.guard';
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
          { path: '', redirectTo: 'usage-stats', pathMatch: 'full' },
          {
            // Management tool — must actually be allowed to manage THIS channel, not just be
            // logged in (channelManagementGuard), unlike the vote-session pages below.
            path: 'usage-stats',
            loadComponent: () => import('./features/usage-stats/usage-stats-page').then((m) => m.UsageStatsPage),
            canActivate: [channelManagementGuard],
          },
          {
            // Requires login only — the list itself has no per-session role restriction.
            path: 'vote-sessions',
            loadComponent: () =>
              import('./features/voting/vote-session-list-page').then((m) => m.VoteSessionListPage),
            canActivate: [authGuard],
          },
          {
            // Requires login AND being part of this specific session's target audience
            // (voteSessionAccessGuard — anonymous share-link viewing was removed, see decision log).
            path: 'vote-sessions/:sessionId',
            loadComponent: () =>
              import('./features/voting/vote-session-detail-page').then((m) => m.VoteSessionDetailPage),
            canActivate: [voteSessionAccessGuard],
          },
        ],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
