/**
 * Web (ADR-0053): sem push nesta fase. O Web Push é P2 (Q-26) e a API recusa `platform: WEB` com `422`. Com a aba
 * aberta, o som e o título da aba já avisam (08 §8.15). Nada aqui toca o `expo-notifications`.
 */
import type { PushNotifications, PushPermission } from './notifications.types';

export type { PushNotifications, PushPermission, PushTokenResult } from './notifications.types';

const UNDETERMINED: PushPermission = { status: 'undetermined', canAskAgain: false };

export const notifications: PushNotifications = {
  supported: false,
  init: () => () => {},
  getPermission: async () => UNDETERMINED,
  requestPermission: async () => UNDETERMINED,
  getToken: async () => ({ kind: 'error', message: 'Push indisponível no navegador.' }),
  onTokenChange: () => () => {},
  openSettings: () => {},
};
