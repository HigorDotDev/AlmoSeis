/**
 * Fatia 5 do EP-002 (ADR-0049, 06 §6.3 "Push dos pedidos do site"): registro do token, supressão em primeiro plano,
 * toque que abre o pedido (inclusive depois do login), tenant diferente ignorado, permissão pedida só depois da
 * explicação, `DELETE` no "Encerrar sessão" do aparelho pessoal e o motivo quando falta o `projectId` do EAS.
 * `expo-notifications` é simulado: o push de verdade exige `eas init` + Firebase e um build novo do Dev Client.
 */
import { act, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react-native';
import { router } from 'expo-router';

import { Platform } from 'react-native';

import { configureApiClient } from '@/api/client';
import type { Me, PushRegistration } from '@/api/types';
import { WebOrderPushPrompt, WebOrderPushSettings, PUSH_NO_PROJECT_TEXT } from '@/features/push/components';
import { OPEN_TARGET_DELAY_MS, usePushNotifications, usePushRegistration, usePushTargetOpener } from '@/features/push/hooks';
import { notifications as webNotifications } from '@/features/push/notifications.web';
import { decidePushTarget, parseWebOrderPushData, PUSH_TARGET_MAX_AGE_MS, usePushStatus, usePushTarget } from '@/features/push/pushShared';
import { resetPushRegistrationForTests, syncPushRegistration, unregisterPush } from '@/features/push/registration';
import { APP_PLATFORM } from '@/config';
import { usePreferences } from '@/stores/preferences';
import { useSession } from '@/stores/session';

import { memory } from '@/test-utils/fakeDb';

jest.mock('expo-router', () => ({ router: { push: jest.fn(), replace: jest.fn() } }));
jest.mock('@/db/metaRepo', () => ({ metaRepo: jest.requireActual('@/test-utils/fakeDb').metaRepo }));

const mockConstants = { expoConfig: { extra: { eas: { projectId: 'proj-123' } } } as { extra?: { eas?: { projectId?: string } } }, easConfig: null };
jest.mock('expo-constants', () => ({
  __esModule: true,
  get default() {
    return mockConstants;
  },
}));

type Listener = (r: unknown) => void;
const mockN = {
  handler: null as null | { handleNotification: () => Promise<Record<string, boolean>> },
  responseListeners: [] as Listener[],
  tokenListeners: [] as (() => void)[],
  lastResponse: null as unknown,
  permission: { status: 'undetermined', canAskAgain: true },
  afterRequest: { status: 'granted', canAskAgain: true },
  token: 'ExponentPushToken[abc123]',
};

jest.mock('expo-notifications', () => ({
  DEFAULT_ACTION_IDENTIFIER: 'expo.modules.notifications.actions.DEFAULT',
  AndroidImportance: { HIGH: 5 },
  AndroidNotificationVisibility: { PUBLIC: 1 },
  setNotificationHandler: jest.fn((h) => {
    mockN.handler = h;
  }),
  setNotificationChannelAsync: jest.fn(async () => null),
  getLastNotificationResponse: jest.fn(() => mockN.lastResponse),
  clearLastNotificationResponse: jest.fn(() => {
    mockN.lastResponse = null;
  }),
  getLastNotificationResponseAsync: jest.fn(async () => mockN.lastResponse),
  addNotificationResponseReceivedListener: jest.fn((l: Listener) => {
    mockN.responseListeners.push(l);
    return { remove: () => (mockN.responseListeners = mockN.responseListeners.filter((x) => x !== l)) };
  }),
  addPushTokenListener: jest.fn((l: () => void) => {
    mockN.tokenListeners.push(l);
    return { remove: () => (mockN.tokenListeners = mockN.tokenListeners.filter((x) => x !== l)) };
  }),
  getPermissionsAsync: jest.fn(async () => mockN.permission),
  requestPermissionsAsync: jest.fn(async () => {
    mockN.permission = mockN.afterRequest;
    return mockN.permission;
  }),
  getExpoPushTokenAsync: jest.fn(async () => ({ type: 'expo', data: mockN.token })),
}));

const Notifications = jest.requireMock('expo-notifications') as Record<string, jest.Mock>;

const TENANT = '0192d6a0-0000-7000-8000-000000000001';
const ORDER = '0192d6a0-0000-7000-8000-000000000504';
const INSTALLATION = 'inst_8f3a2c1e9b7d4f60';

/** Exemplo literal do 06 §6.3 (`200` do `PUT /devices/push-token`). */
const registration: PushRegistration = {
  installationId: INSTALLATION,
  platform: 'ANDROID',
  webOrdersEnabled: true,
  receivesWebOrders: true,
  registeredAt: '2026-11-20T14:58:00Z',
  updatedAt: '2026-11-20T14:58:00Z',
};

const json = (status: number, body: unknown) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const calls: { method: string; path: string; body: unknown }[] = [];
let putResponse: () => Response = () => json(200, registration);

function signIn(permissions: Me['permissions'], tenantId = TENANT, employeeId = 'e1') {
  useSession.setState({
    status: 'signedIn',
    installationId: INSTALLATION,
    sharedDevice: null,
    me: {
      tenant: { id: tenantId, name: 'Marmitaria da Ana', timeZone: 'America/Sao_Paulo' },
      employee: { id: employeeId, displayName: 'Ana', roles: ['OWNER'], hasPin: false },
      permissions,
      subscription: { canWrite: true },
    } as unknown as Me,
  });
}

function pushResponse(data: unknown, id = 'n1') {
  return { actionIdentifier: 'expo.modules.notifications.actions.DEFAULT', notification: { request: { identifier: id, content: { data } } } };
}

const pushData = { tenantId: TENANT, orderId: ORDER, kind: 'WEB_ORDER_AWAITING_ACCEPTANCE' };

beforeEach(() => {
  jest.clearAllMocks();
  memory.reset();
  resetPushRegistrationForTests();
  usePushStatus.setState({ permission: null, registration: null, unavailable: null, errorMessage: null });
  usePushTarget.setState({ target: null });
  usePreferences.setState({ webOrdersPush: null, pushPromptDismissed: false });
  mockN.handler = null;
  mockN.responseListeners = [];
  mockN.tokenListeners = [];
  mockN.lastResponse = null;
  mockN.permission = { status: 'granted', canAskAgain: true };
  mockN.afterRequest = { status: 'granted', canAskAgain: true };
  mockN.token = 'ExponentPushToken[abc123]';
  mockConstants.expoConfig = { extra: { eas: { projectId: 'proj-123' } } };
  calls.length = 0;
  putResponse = () => json(200, registration);
  configureApiClient({
    getAccessToken: () => 'tok',
    refresh: async () => true,
    onSessionExpired: () => {},
    onUpgradeRequired: () => {},
    onSubscriptionInactive: () => {},
  });
  global.fetch = jest.fn(async (url: string, init: RequestInit) => {
    const path = url.replace(/^https?:\/\/[^/]+(\/api\/v1)?/, '');
    const method = init.method ?? 'GET';
    calls.push({ method, path, body: init.body ? JSON.parse(init.body as string) : undefined });
    if (path === '/devices/push-token' && method === 'PUT') return putResponse();
    if (path === '/devices/push-token' && method === 'DELETE') return new Response(null, { status: 204 });
    return json(404, { errorCode: 'NotFound' });
  }) as unknown as typeof fetch;
  signIn(['orders.view', 'orders.accept']);
});

// `replaceProperty` (Platform.OS) e `spyOn` voltam ao normal entre os testes.
afterEach(() => jest.restoreAllMocks());

const puts = () => calls.filter((c) => c.method === 'PUT' && c.path === '/devices/push-token');
const deletes = () => calls.filter((c) => c.method === 'DELETE' && c.path === '/devices/push-token');

describe('data da notificação (06 §6.3)', () => {
  it('aceita só tenantId, orderId e kind conhecidos', () => {
    expect(parseWebOrderPushData(pushData)).toEqual(pushData);
    expect(parseWebOrderPushData({ ...pushData, kind: 'WEB_ORDER_REMINDER' })?.kind).toBe('WEB_ORDER_REMINDER');
    expect(parseWebOrderPushData({ ...pushData, kind: 'ORDER_CREATED' })).toBeNull();
    expect(parseWebOrderPushData({ ...pushData, orderId: '../menu' })).toBeNull();
    expect(parseWebOrderPushData({ orderId: ORDER, kind: pushData.kind })).toBeNull();
    expect(parseWebOrderPushData(null)).toBeNull();
  });

  it('sem sessão espera; outro estabelecimento ignora; toque velho some', () => {
    const t = { ...pushData, kind: 'WEB_ORDER_AWAITING_ACCEPTANCE' as const, tappedAt: 1_000 };
    expect(decidePushTarget(t, null, 2_000)).toBe('wait');
    expect(decidePushTarget(t, TENANT, 2_000)).toBe('open');
    expect(decidePushTarget(t, 'outro-tenant', 2_000)).toBe('drop');
    expect(decidePushTarget(t, TENANT, 1_000 + PUSH_TARGET_MAX_AGE_MS + 1)).toBe('drop');
  });
});

describe('registro (PUT /devices/push-token)', () => {
  it('manda o corpo do contrato e guarda a resposta sem o token', async () => {
    await syncPushRegistration();
    expect(puts()).toHaveLength(1);
    expect(puts()[0]!.body).toEqual({ installationId: INSTALLATION, expoPushToken: 'ExponentPushToken[abc123]', platform: APP_PLATFORM, webOrdersEnabled: null });
    expect(Notifications.getExpoPushTokenAsync).toHaveBeenCalledWith({ projectId: 'proj-123' });
    expect(usePushStatus.getState().registration).toEqual(registration);
    expect(memory.meta.get('pushRegisteredTenant')).toBe(TENANT);
  });

  it('não repete o PUT com os mesmos dados; repete com outro funcionário (PIN) ou token novo', async () => {
    await syncPushRegistration();
    await syncPushRegistration();
    expect(puts()).toHaveLength(1);
    signIn(['orders.view'], TENANT, 'e2');
    await syncPushRegistration();
    expect(puts()).toHaveLength(2);
    mockN.token = 'ExponentPushToken[novo]';
    await syncPushRegistration();
    expect(puts()).toHaveLength(3);
    expect(puts()[2]!.body).toMatchObject({ expoPushToken: 'ExponentPushToken[novo]' });
  });

  it('409 PushTokenInUse: sem erro, estado local mantido e nova tentativa na próxima abertura, sem as 12 h', async () => {
    await syncPushRegistration();
    expect(puts()).toHaveLength(1);
    putResponse = () =>
      new Response(JSON.stringify({ status: 409, errorCode: 'PushTokenInUse', title: 'PushTokenInUse' }), { status: 409, headers: { 'Content-Type': 'application/problem+json' } });
    await syncPushRegistration({ force: true });
    expect(puts()).toHaveLength(2);
    expect(usePushStatus.getState()).toMatchObject({ registration, unavailable: null, errorMessage: null });
    expect(memory.meta.get('pushRegisteredTenant')).toBe(TENANT);
    // Próxima abertura (sem force, mesmos dados, dentro das 12 h): tenta de novo.
    putResponse = () => json(200, registration);
    await syncPushRegistration();
    expect(puts()).toHaveLength(3);
    await syncPushRegistration();
    expect(puts()).toHaveLength(3);
  });

  it('sem permissão não registra; se este aparelho tinha registrado, apaga', async () => {
    mockN.permission = { status: 'denied', canAskAgain: false };
    await syncPushRegistration();
    expect(puts()).toHaveLength(0);
    expect(deletes()).toHaveLength(0);
    memory.meta.set('pushRegisteredTenant', TENANT);
    await syncPushRegistration();
    expect(deletes()).toHaveLength(1);
    expect(memory.meta.has('pushRegisteredTenant')).toBe(false);
  });

  it('sem projectId do EAS: não registra, avisa uma vez em dev e mostra o motivo em Configurações', async () => {
    mockConstants.expoConfig = { extra: {} };
    const warn = jest.spyOn(console, 'warn').mockImplementation(() => {});
    await syncPushRegistration();
    await syncPushRegistration({ force: true });
    expect(puts()).toHaveLength(0);
    expect(Notifications.getExpoPushTokenAsync).not.toHaveBeenCalled();
    expect(warn).toHaveBeenCalledTimes(1);
    warn.mockRestore();
    expect(usePushStatus.getState().unavailable).toBe('noProjectId');
    await render(<WebOrderPushSettings />);
    expect(screen.getByText(PUSH_NO_PROJECT_TEXT)).toBeTruthy();
  });

  it('a chave "Avisar pedidos do site" vai no PUT', async () => {
    putResponse = () => json(200, { ...registration, webOrdersEnabled: false, receivesWebOrders: false });
    await render(<WebOrderPushSettings />);
    await act(async () => {
      await fireEvent(screen.getByLabelText('Avisar pedidos do site com o app fechado'), 'valueChange', false);
    });
    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0]!.body).toMatchObject({ webOrdersEnabled: false });
    expect(usePreferences.getState().webOrdersPush).toBe(false);
    expect(await screen.findByText('Desligado: este aparelho não recebe o aviso com o app fechado.')).toBeTruthy();
  });

  it('perfil sem orders.accept: registrado, mas a tela diz que não recebe', async () => {
    putResponse = () => json(200, { ...registration, receivesWebOrders: false });
    await syncPushRegistration();
    await render(<WebOrderPushSettings />);
    expect(screen.getByText('Seu perfil não aceita pedidos do site: este aparelho não recebe o aviso.')).toBeTruthy();
  });

  it('registra ao entrar e de novo quando o token muda', async () => {
    const { unmount } = await renderHook(() => usePushRegistration());
    await waitFor(() => expect(puts()).toHaveLength(1));
    mockN.token = 'ExponentPushToken[trocado]';
    await act(async () => {
      mockN.tokenListeners.forEach((l) => l());
    });
    await waitFor(() => expect(puts()).toHaveLength(2));
    await unmount();
    expect(mockN.tokenListeners).toHaveLength(0);
  });

  it('"Encerrar sessão" no aparelho pessoal manda o DELETE', async () => {
    await syncPushRegistration();
    await unregisterPush();
    expect(deletes()).toHaveLength(1);
    expect(usePushStatus.getState().registration).toBeNull();
    expect(memory.meta.has('pushRegisteredTenant')).toBe(false);
  });
});

describe('permissão (ADR-0049 item 6)', () => {
  it('explica antes; o pedido do sistema só sai no "Permitir avisos" e então registra', async () => {
    mockN.permission = { status: 'undetermined', canAskAgain: true };
    await syncPushRegistration();
    expect(Notifications.requestPermissionsAsync).not.toHaveBeenCalled();
    await render(<WebOrderPushPrompt />);
    expect(screen.getByText(/Para não perder pedidos do site/)).toBeTruthy();
    await act(async () => {
      await fireEvent.press(screen.getByText('Permitir avisos'));
    });
    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(Notifications.requestPermissionsAsync).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Permitir avisos')).toBeNull();
  });

  it('"Agora não" esconde e não pede ao sistema', async () => {
    mockN.permission = { status: 'undetermined', canAskAgain: true };
    await syncPushRegistration();
    await render(<WebOrderPushPrompt />);
    await fireEvent.press(screen.getByText('Agora não'));
    expect(screen.queryByText(/Para não perder pedidos do site/)).toBeNull();
    expect(usePreferences.getState().pushPromptDismissed).toBe(true);
    expect(Notifications.requestPermissionsAsync).not.toHaveBeenCalled();
  });

  it('negada: aviso discreto com atalho para as configurações', async () => {
    mockN.permission = { status: 'denied', canAskAgain: false };
    await syncPushRegistration();
    await render(<WebOrderPushPrompt />);
    expect(screen.getByTestId('push-denied-notice')).toBeTruthy();
    const { Linking } = jest.requireActual('react-native') as typeof import('react-native');
    const open = jest.spyOn(Linking, 'openSettings').mockResolvedValue();
    await fireEvent.press(screen.getByText('Configurações'));
    expect(open).toHaveBeenCalled();
  });

  it('quem não aceita pedidos do site não vê a explicação', async () => {
    signIn(['orders.view']);
    mockN.permission = { status: 'undetermined', canAskAgain: true };
    await syncPushRegistration();
    await render(<WebOrderPushPrompt />);
    expect(screen.queryByText(/Para não perder pedidos do site/)).toBeNull();
  });
});

describe('recebimento', () => {
  it('em primeiro plano a notificação do sistema é suprimida e o canal Android web-orders é criado', async () => {
    jest.replaceProperty(Platform, 'OS', 'android');
    await renderHook(() => usePushNotifications());
    expect(mockN.handler).not.toBeNull();
    await expect(mockN.handler!.handleNotification()).resolves.toEqual({ shouldShowBanner: false, shouldShowList: false, shouldPlaySound: false, shouldSetBadge: false });
    expect(Notifications.setNotificationChannelAsync).toHaveBeenCalledWith('web-orders', expect.objectContaining({ importance: 5, sound: 'web_order.wav' }));
  });

  it('tocar abre o pedido do estabelecimento da sessão', async () => {
    jest.useFakeTimers();
    try {
      await renderHook(() => {
        usePushNotifications();
        usePushTargetOpener();
      });
      await act(async () => {
        mockN.responseListeners.forEach((l) => l(pushResponse(pushData)));
      });
      await act(async () => {
        jest.advanceTimersByTime(OPEN_TARGET_DELAY_MS);
      });
      expect(router.push).toHaveBeenCalledWith(`/orders/${ORDER}`);
      expect(usePushTarget.getState().target).toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  it('notificação de outro estabelecimento é ignorada', async () => {
    jest.useFakeTimers();
    try {
      await renderHook(() => {
        usePushNotifications();
        usePushTargetOpener();
      });
      await act(async () => {
        mockN.responseListeners.forEach((l) => l(pushResponse({ ...pushData, tenantId: '0192d6a0-0000-7000-8000-000000000999' })));
      });
      await act(async () => {
        jest.advanceTimersByTime(OPEN_TARGET_DELAY_MS * 2);
      });
      expect(router.push).not.toHaveBeenCalled();
      expect(usePushTarget.getState().target).toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  it('sem sessão: o toque que abriu o app espera o login e depois abre o pedido', async () => {
    jest.useFakeTimers();
    try {
      useSession.setState({ status: 'signedOut', me: null });
      mockN.lastResponse = pushResponse(pushData, 'cold');
      await renderHook(() => {
        usePushNotifications();
        usePushTargetOpener();
      });
      await act(async () => {
        jest.advanceTimersByTime(OPEN_TARGET_DELAY_MS * 2);
      });
      expect(router.push).not.toHaveBeenCalled();
      expect(usePushTarget.getState().target).toMatchObject({ orderId: ORDER });
      await act(async () => {
        signIn(['orders.view', 'orders.accept']);
      });
      await act(async () => {
        jest.advanceTimersByTime(OPEN_TARGET_DELAY_MS);
      });
      expect(router.push).toHaveBeenCalledWith(`/orders/${ORDER}`);
      expect(Notifications.clearLastNotificationResponse).toHaveBeenCalled();
    } finally {
      jest.useRealTimers();
    }
  });

  it('a mesma notificação não abre duas vezes', async () => {
    await renderHook(() => usePushNotifications());
    await act(async () => {
      mockN.responseListeners.forEach((l) => l(pushResponse(pushData, 'same')));
    });
    usePushTarget.setState({ target: null });
    await act(async () => {
      mockN.responseListeners.forEach((l) => l(pushResponse(pushData, 'same')));
    });
    expect(usePushTarget.getState().target).toBeNull();
  });
});

describe('web (ADR-0053): sem push nesta fase', () => {
  it('não registra nem pede permissão', async () => {
    expect(webNotifications.supported).toBe(false);
    expect(await webNotifications.getPermission()).toEqual({ status: 'undetermined', canAskAgain: false });
    expect((await webNotifications.getToken()).kind).toBe('error');
    const off = webNotifications.init(() => {});
    off();
  });
});
