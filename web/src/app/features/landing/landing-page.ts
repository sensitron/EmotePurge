import { Component, inject } from '@angular/core';

import { AuthService } from '../../core/auth/auth.service';

interface Feature {
  icon: string;
  title: string;
  description: string;
  badge?: string;
}

interface Step {
  title: string;
  description: string;
}

const FEATURES: Feature[] = [
  {
    icon: '💬',
    title: 'Chat-Analytics',
    description:
      'EmotePurge verfolgt live, welche Emotes in deinem Chat tatsächlich benutzt werden — pro Emote, über frei wählbare Zeiträume.',
  },
  {
    icon: '🗳️',
    title: 'Community-Voting',
    badge: 'Optional',
    description:
      'Lass zusätzlich Zuschauer, Subs oder Mods per Daumen hoch/runter abstimmen. Kein Muss — die Chat-Analytics reichen allein schon aus, um Emotes zum Löschen auszuwählen.',
  },
  {
    icon: '🧹',
    title: '7TV Mass-Delete',
    description:
      'Ungewollte Emotes markieren und in einem Rutsch direkt aus deinem 7TV-Set entfernen — kein manuelles Klicken einzeln durch die Liste.',
  },
  {
    icon: '🔐',
    title: 'Twitch-Login & Rollen',
    description:
      'Sicherer Login über Twitch selbst. Broadcaster, Mods und Admins bekommen automatisch genau die Rechte, die ihnen zustehen.',
  },
];

const STEPS: Step[] = [
  { title: 'Mit Twitch einloggen', description: 'Ein Klick genügt — kein neues Passwort, keine separate Registrierung.' },
  { title: 'Channel beitreten', description: 'Der Bot joint deinen Chat und synct dein aktives 7TV-Set automatisch.' },
  {
    title: 'Nutzung tracken',
    description: 'Chat-Nutzung wird automatisch getrackt — Community-Voting kannst du zusätzlich starten, musst du aber nicht.',
  },
  { title: 'Aufräumen', description: 'Unerwünschte Emotes auswählen und gebündelt aus 7TV entfernen.' },
];

@Component({
  selector: 'app-landing-page',
  templateUrl: './landing-page.html',
})
export class LandingPage {
  private readonly authService = inject(AuthService);

  protected readonly features = FEATURES;
  protected readonly steps = STEPS;

  protected login(): void {
    this.authService.login();
  }
}
