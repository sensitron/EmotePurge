import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
import { homeGuard } from './core/auth/home.guard';
import { usageStatsAccessGuard } from './core/channels/usage-stats-access.guard';
import { voteSessionAccessGuard } from './core/voting/vote-session-access.guard';
import { LoginPage } from './features/login/login-page';

export const routes: Routes = [
  {
    // Public marketing page, no guard — first stop for anonymous visitors, e.g. via a shared link.
    path: 'welcome',
    loadComponent: () => import('./features/landing/landing-page').then((m) => m.LandingPage),
  },
  { path: 'login', component: LoginPage },
  {
    path: '',
    loadComponent: () => import('./features/shell/app-shell').then((m) => m.AppShell),
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./features/overview/overview-page').then((m) => m.OverviewPage),
        canActivate: [homeGuard],
      },
      {
        // Cross-channel — a user's voting history isn't scoped to a single channelName route value,
        // so this is a sibling of the overview route, not nested under channels/:channelName.
        path: 'my-votings',
        loadComponent: () =>
          import('./features/my-votings/my-votings-page').then((m) => m.MyVotingsPage),
        canActivate: [authGuard],
      },
      {
        path: 'channels/:channelName',
        loadComponent: () =>
          import('./features/channel-workspace/channel-workspace-layout').then(
            (m) => m.ChannelWorkspaceLayout,
          ),
        children: [
          { path: '', redirectTo: 'usage-stats', pathMatch: 'full' },
          {
            // Must actually be allowed to view THIS channel's usage stats (Twitch mod/broadcaster/
            // admin OR 7TV editor — usageStatsAccessGuard), not just be logged in, unlike the
            // vote-session pages below.
            path: 'usage-stats',
            loadComponent: () =>
              import('./features/usage-stats/usage-stats-page').then((m) => m.UsageStatsPage),
            canActivate: [usageStatsAccessGuard],
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
              import('./features/voting/vote-session-detail-page').then(
                (m) => m.VoteSessionDetailPage,
              ),
            canActivate: [voteSessionAccessGuard],
          },
        ],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
