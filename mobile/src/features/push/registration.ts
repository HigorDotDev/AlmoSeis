/**
 * Registro do aparelho para o push dos pedidos do site (ADR-0049 item 4, 06 §6.3): `PUT /devices/push-token` ao
 * entrar, a cada abertura (e volta ao primeiro plano, no máximo a cada 12 h com os mesmos dados) e quando o token
 * muda; `DELETE` no "Encerrar sessão" do aparelho pessoal. O servidor decide quem recebe no envio; aqui só se
 * informa o token, a instalação e a opção "Avisar pedidos do site com o app fechado".
 */
import { pushTokenApi } from '@/api/endpoints/tenant';
import { errorMessage } from '@/api/errors';
import { APP_PLATFORM } from '@/config';
import { metaRepo } from '@/db/metaRepo';
import { usePreferences } from '@/stores/preferences';
import { useSession } from '@/stores/session';

import { notifications } from './notifications';
import { usePushStatus } from './pushShared';

/** Estabelecimento em que este aparelho ficou registrado (para apagar se a permissão for retirada). */
const REGISTERED_KEY = 'pushRegisteredTenant';
const REFRESH_MS = 12 * 60 * 60_000;
const DELETE_TIMEOUT_MS = 5_000;

let last: { key: string; at: number } | null = null;
let loggedNoProjectId = false;
let running: Promise<void> | null = null;
let again: { force: boolean } | null = null;

/** Testes. */
export function resetPushRegistrationForTests(): void {
  last = null;
  loggedNoProjectId = false;
  running = null;
  again = null;
}

/** Uma sincronização por vez; quem pedir durante ela ganha uma rodada logo depois. */
export function syncPushRegistration(options: { force?: boolean; now?: number } = {}): Promise<void> {
  if (running) {
    again = { force: (again?.force ?? false) || !!options.force };
    return running;
  }
  running = (async () => {
    try {
      await syncOnce(options);
      while (again) {
        const next = again;
        again = null;
        await syncOnce(next);
      }
    } finally {
      running = null;
    }
  })();
  return running;
}

async function syncOnce({ force = false, now = Date.now() }: { force?: boolean; now?: number }): Promise<void> {
  const status = usePushStatus.getState();
  if (!notifications.supported || APP_PLATFORM === 'WEB') {
    status.set({ unavailable: 'web' });
    return;
  }
  const { me, installationId, status: sessionStatus } = useSession.getState();
  if (sessionStatus !== 'signedIn' || !me || !installationId) return;

  const permission = await notifications.getPermission();
  status.set({ permission });
  if (permission.status !== 'granted') {
    // Sem permissão o aviso não aparece: não registra, e apaga o registro que este aparelho tinha feito.
    await dropRegistration(me.tenant.id);
    return;
  }

  const token = await notifications.getToken();
  if (token.kind === 'noProjectId') {
    if (__DEV__ && !loggedNoProjectId) {
      loggedNoProjectId = true;
      console.warn('[push] extra.eas.projectId ausente no app.json (falta o `eas init`): o aparelho não registra o push.');
    }
    status.set({ unavailable: 'noProjectId', registration: null, errorMessage: null });
    return;
  }
  if (token.kind === 'error') {
    status.set({ unavailable: 'error', errorMessage: token.message });
    return;
  }

  const webOrdersEnabled = usePreferences.getState().webOrdersPush;
  const key = [me.tenant.id, me.employee.id, installationId, token.token, String(webOrdersEnabled)].join('|');
  if (!force && last?.key === key && now - last.at < REFRESH_MS) return;

  try {
    const { data } = await pushTokenApi.put({ installationId, expoPushToken: token.token, platform: APP_PLATFORM, webOrdersEnabled });
    last = { key, at: now };
    status.set({ registration: data, unavailable: null, errorMessage: null });
    await metaRepo.set(REGISTERED_KEY, me.tenant.id);
  } catch (e) {
    // Sem rede ou recusa: tenta de novo na próxima abertura ou volta ao primeiro plano.
    status.set({ unavailable: 'error', errorMessage: errorMessage(e) });
  }
}

async function dropRegistration(tenantId: string): Promise<void> {
  usePushStatus.getState().set({ registration: null });
  last = null;
  if ((await metaRepo.get(REGISTERED_KEY)) !== tenantId) return;
  try {
    await pushTokenApi.remove(DELETE_TIMEOUT_MS);
    await metaRepo.remove(REGISTERED_KEY);
  } catch {
    // Fica marcado e tenta na próxima abertura.
  }
}

/**
 * "Encerrar sessão" no aparelho pessoal: `DELETE` antes do logout (o `POST /auth/logout` também apaga, mas sem
 * rede o logout não chega e o aparelho continuaria avisando). No aparelho compartilhado não se chama: o registro
 * fica com o aparelho (06 §6.3).
 */
export async function unregisterPush(): Promise<void> {
  if (!notifications.supported) return;
  last = null;
  usePushStatus.getState().reset();
  try {
    await pushTokenApi.remove(DELETE_TIMEOUT_MS);
  } catch {
    // Sem rede: o logout (ou os 30 dias sem `PUT`) apaga no servidor.
  }
  await metaRepo.remove(REGISTERED_KEY).catch(() => {});
}

/** Mostra o pedido do sistema (depois da explicação) e, permitido, registra. */
export async function requestPushPermission(): Promise<void> {
  if (!notifications.supported) return;
  const permission = await notifications.requestPermission();
  usePushStatus.getState().set({ permission });
  if (permission.status === 'granted') await syncPushRegistration({ force: true });
}

/** A chave "Avisar pedidos do site com o app fechado": grava no aparelho e manda no `PUT`. */
export async function setWebOrdersPush(on: boolean): Promise<void> {
  usePreferences.getState().setWebOrdersPush(on);
  if (!notifications.supported) return;
  const permission = usePushStatus.getState().permission;
  if (on && permission?.status === 'undetermined' && permission.canAskAgain) {
    await requestPushPermission();
    return;
  }
  await syncPushRegistration({ force: true });
}
