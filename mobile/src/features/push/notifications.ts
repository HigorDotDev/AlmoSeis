/**
 * Push no celular (ADR-0049): `expo-notifications` → serviço da Expo → FCM/APNs. Web: `notifications.web.ts` (no-op).
 * Módulo nativo: entrou com a fatia 5 do EP-002 e exige um build novo do Dev Client/APK (muda o fingerprint).
 */
import Constants from 'expo-constants';
import * as Notifications from 'expo-notifications';
import { AndroidImportance, AndroidNotificationVisibility } from 'expo-notifications';
import { Linking, Platform } from 'react-native';

import type { PushNotifications, PushPermission, PushTokenResult } from './notifications.types';
import { WEB_ORDERS_CHANNEL_ID, WEB_ORDERS_SOUND } from './pushShared';

export type { PushNotifications, PushPermission, PushTokenResult } from './notifications.types';

let channelReady: Promise<void> = Promise.resolve();

/** Android 13+: o pedido de permissão só aparece depois de existir um canal; por isso ele é criado na raiz. */
function ensureChannel(): Promise<void> {
  if (Platform.OS !== 'android') return Promise.resolve();
  channelReady = Notifications.setNotificationChannelAsync(WEB_ORDERS_CHANNEL_ID, {
    name: 'Pedidos do site',
    description: 'Pedido do site esperando aceite, com o app fechado.',
    importance: AndroidImportance.HIGH,
    sound: WEB_ORDERS_SOUND,
    vibrationPattern: [0, 400, 200, 400],
    lockscreenVisibility: AndroidNotificationVisibility.PUBLIC,
  })
    .then(() => undefined)
    .catch(() => undefined);
  return channelReady;
}

function toPermission(p: Notifications.NotificationPermissionsStatus): PushPermission {
  const status = p.status === 'granted' ? 'granted' : p.status === 'denied' ? 'denied' : 'undetermined';
  return { status, canAskAgain: p.canAskAgain };
}

function projectId(): string | null {
  const extra = Constants.expoConfig?.extra as { eas?: { projectId?: string } } | undefined;
  return extra?.eas?.projectId ?? Constants.easConfig?.projectId ?? null;
}

export const notifications: PushNotifications = {
  supported: true,

  init(onOpen) {
    // Primeiro plano: nada de notificação do sistema (o SignalR + som já avisaram, ADR-0049 item 9). Vale também
    // para a de outro estabelecimento, que é ignorada.
    Notifications.setNotificationHandler({
      handleNotification: async () => ({ shouldShowBanner: false, shouldShowList: false, shouldPlaySound: false, shouldSetBadge: false }),
    });
    void ensureChannel();

    const handled = new Set<string>();
    const handle = (response: Notifications.NotificationResponse | null) => {
      if (!response || response.actionIdentifier !== Notifications.DEFAULT_ACTION_IDENTIFIER) return;
      const id = response.notification.request.identifier;
      if (handled.has(id)) return;
      handled.add(id);
      onOpen(response.notification.request.content.data);
      try {
        Notifications.clearLastNotificationResponse();
      } catch {
        // Versões sem o método síncrono: a lista `handled` já evita abrir duas vezes.
      }
    };
    // O toque que abriu o app (app fechado) chega antes de qualquer ouvinte.
    try {
      handle(Notifications.getLastNotificationResponse());
    } catch {
      void Notifications.getLastNotificationResponseAsync().then(handle).catch(() => {});
    }
    const sub = Notifications.addNotificationResponseReceivedListener(handle);
    return () => sub.remove();
  },

  async getPermission() {
    return toPermission(await Notifications.getPermissionsAsync());
  },

  async requestPermission() {
    await channelReady;
    return toPermission(await Notifications.requestPermissionsAsync({ ios: { allowAlert: true, allowSound: true, allowBadge: false } }));
  },

  async getToken(): Promise<PushTokenResult> {
    const id = projectId();
    if (!id) return { kind: 'noProjectId' };
    try {
      await channelReady;
      const token = await Notifications.getExpoPushTokenAsync({ projectId: id });
      return { kind: 'ok', token: token.data };
    } catch (e) {
      return { kind: 'error', message: e instanceof Error ? e.message : String(e) };
    }
  },

  onTokenChange(listener) {
    const sub = Notifications.addPushTokenListener(() => listener());
    return () => sub.remove();
  },

  openSettings() {
    void Linking.openSettings().catch(() => {});
  },
};
