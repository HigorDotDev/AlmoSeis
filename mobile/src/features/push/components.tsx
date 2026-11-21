/**
 * Telas do push dos pedidos do site (ADR-0049 item 6): a explicação antes do pedido de permissão, o aviso discreto
 * quando ela foi negada e a seção de Configurações. Na web não aparecem (sem push nesta fase, ADR-0053).
 */
import { useState } from 'react';
import { Switch, View } from 'react-native';

import { Button, Card, IconButton, Row, Section, space, Text } from '@/components/ui';
import { usePreferences } from '@/stores/preferences';
import { usePermission, useSession } from '@/stores/session';

import { notifications } from './notifications';
import { usePushStatus } from './pushShared';
import { requestPushPermission, setWebOrdersPush } from './registration';

export const PUSH_EXPLANATION =
  'Para não perder pedidos do site, permita as notificações. O AlmoSeis avisa com som quando chega um pedido do site esperando aceite, mesmo com o app fechado.';

export const PUSH_DENIED_TEXT = 'Avisos de pedidos do site com o app fechado estão bloqueados neste aparelho.';

export const PUSH_NO_PROJECT_TEXT =
  'Avisos com o app fechado indisponíveis nesta versão do app: o projeto ainda não foi configurado no EAS (falta o projectId).';

/**
 * No primeiro uso dos pedidos do site (faixa "Novos do site" e tela "Página de pedidos"), para quem tem
 * `orders.accept`: a explicação com "Permitir avisos" (só então o pedido do sistema) ou, se negada, uma linha com
 * o atalho para as configurações. "Agora não" esconde os dois; a opção continua em Configurações gerais.
 */
export function WebOrderPushPrompt() {
  const canAccept = usePermission('orders.accept');
  const permission = usePushStatus((s) => s.permission);
  const unavailable = usePushStatus((s) => s.unavailable);
  const dismissed = usePreferences((s) => s.pushPromptDismissed);
  const dismiss = usePreferences((s) => s.dismissPushPrompt);
  const [asking, setAsking] = useState(false);
  if (!notifications.supported || !canAccept || !permission || dismissed || unavailable === 'noProjectId') return null;
  if (permission.status === 'granted') return null;

  if (permission.status === 'undetermined' && permission.canAskAgain) {
    return (
      <Card>
        <Text style={{ fontWeight: '700' }}>Avisar pedidos do site com o app fechado?</Text>
        <Text>{PUSH_EXPLANATION}</Text>
        <Row style={{ flexWrap: 'wrap' }}>
          <Button
            title="Permitir avisos"
            loading={asking}
            onPress={() => {
              setAsking(true);
              void requestPushPermission().finally(() => setAsking(false));
            }}
          />
          <Button title="Agora não" variant="ghost" onPress={dismiss} />
        </Row>
      </Card>
    );
  }

  return (
    <View testID="push-denied-notice" style={{ flexDirection: 'row', alignItems: 'center', gap: space.sm }}>
      <Text variant="small" muted style={{ flex: 1 }}>
        {PUSH_DENIED_TEXT}
      </Text>
      <Button title="Configurações" variant="ghost" onPress={notifications.openSettings} />
      <IconButton icon="close" label="Esconder aviso" onPress={dismiss} />
    </View>
  );
}

/** Configurações gerais → "Pedidos do site com o app fechado". */
export function WebOrderPushSettings() {
  const permission = usePushStatus((s) => s.permission);
  const registration = usePushStatus((s) => s.registration);
  const unavailable = usePushStatus((s) => s.unavailable);
  const error = usePushStatus((s) => s.errorMessage);
  const localPref = usePreferences((s) => s.webOrdersPush);
  const sharedDevice = useSession((s) => s.sharedDevice);
  const [busy, setBusy] = useState(false);

  if (!notifications.supported) {
    return (
      <Section title="Pedidos do site com o app fechado">
        <Text muted>No navegador ainda não há aviso com a página fechada. Deixe a aba aberta: o som e o título da aba avisam os pedidos do site.</Text>
      </Section>
    );
  }

  const enabled = localPref ?? registration?.webOrdersEnabled ?? true;
  const run = (fn: () => Promise<void>) => {
    setBusy(true);
    void fn().finally(() => setBusy(false));
  };
  const blocked = permission !== null && permission.status !== 'granted' && !(permission.status === 'undetermined' && permission.canAskAgain);

  return (
    <Section title="Pedidos do site com o app fechado">
      <Row style={{ justifyContent: 'space-between' }}>
        <Text style={{ flex: 1 }}>Avisar pedidos do site com o app fechado</Text>
        <Switch value={enabled} disabled={busy} onValueChange={(on) => run(() => setWebOrdersPush(on))} accessibilityLabel="Avisar pedidos do site com o app fechado" />
      </Row>
      {unavailable === 'noProjectId' ? <Text variant="small" muted>{PUSH_NO_PROJECT_TEXT}</Text> : null}
      {permission?.status === 'undetermined' && permission.canAskAgain ? (
        <>
          <Text variant="small" muted>
            {PUSH_EXPLANATION}
          </Text>
          <Button title="Permitir notificações" variant="secondary" loading={busy} onPress={() => run(requestPushPermission)} />
        </>
      ) : null}
      {blocked ? (
        <>
          <Text variant="small" muted>
            {PUSH_DENIED_TEXT} Libere as notificações do AlmoSeis nas configurações do aparelho.
          </Text>
          <Button title="Abrir configurações do aparelho" variant="secondary" onPress={notifications.openSettings} />
        </>
      ) : null}
      {permission?.status === 'granted' && registration ? (
        <Text variant="small" muted testID="push-registration-status">
          {!registration.webOrdersEnabled
            ? 'Desligado: este aparelho não recebe o aviso com o app fechado.'
            : registration.receivesWebOrders
              ? 'Este aparelho recebe o aviso dos pedidos do site com o app fechado.'
              : 'Seu perfil não aceita pedidos do site: este aparelho não recebe o aviso.'}
        </Text>
      ) : null}
      {permission?.status === 'granted' && unavailable === 'error' && error ? (
        <Text variant="small" muted>
          Não foi possível registrar o aparelho agora ({error}). O app tenta de novo na próxima abertura.
        </Text>
      ) : null}
      {sharedDevice ? (
        <Text variant="small" muted>
          Aparelho compartilhado: o aviso segue o último funcionário que entrou por PIN, mesmo depois do tempo sem uso.
        </Text>
      ) : null}
      <Text variant="small" muted>
        Alguns Android (Xiaomi, Samsung) atrasam o aviso com a economia de bateria: tire o AlmoSeis da otimização de bateria.
      </Text>
    </Section>
  );
}
