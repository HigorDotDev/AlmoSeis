import { router, Stack } from 'expo-router';
import { useState } from 'react';

import type { OpenBusinessDayResult } from '@/api/types';
import { Banner, Button, ConfirmDialog, MoneyInput, Screen, Text } from '@/components/ui';
import { useClosing, useClosingHistory } from '@/features/closing/hooks';
import { formatBRL } from '@/lib/money';
import { useIsOnline } from '@/stores/connectivity';
import { useCanWrite } from '@/stores/session';

import { useCurrentBusinessDay, useOpenBusinessDay } from '../hooks';

const menuTypeNames = { MARMITA: 'marmitas', PF: 'PF' } as const;

/** 08 §8.9 `business-day/open`. */
export default function OpenBusinessDayScreen() {
  const current = useCurrentBusinessDay();
  const lastClosing = useClosingHistory({});
  const canOpen = useCanWrite('cash.open');
  const online = useIsOnline();
  const [typed, setBalance] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<null | { confirmSameDate: boolean }>(null);
  const [opened, setOpened] = useState<OpenBusinessDayResult | null>(null);

  const open = useOpenBusinessDay(
    (result) => {
      setConfirm(null);
      setOpened(result);
    },
    () => setConfirm({ confirmSameDate: true }),
  );

  // Sugestão: o dinheiro contado no último fechamento (o histórico não traz o valor contado; o detalhe traz).
  const last = useClosing(lastClosing.data?.pages[0]?.items[0]?.dailyClosingId);
  const balance = typed ?? last.data?.cash.countedCash ?? '0.00';

  if (opened) {
    return (
      <Screen>
        <Stack.Screen options={{ title: 'Expediente aberto' }} />
        <Banner tone="success" text={`Expediente aberto com ${formatBRL(opened.businessDay.openingBalance)} em caixa.`} />
        {opened.materializedMenus.length > 0 ? (
          opened.materializedMenus.map((m) => <Text key={m.id}>Cardápio de {menuTypeNames[m.menuType]} criado a partir do modelo semanal.</Text>)
        ) : (
          <Text muted>Nenhum modelo semanal para hoje. Monte o cardápio ou reaproveite um anterior nas abas.</Text>
        )}
        <Button title="Ir para os pedidos" onPress={() => router.replace('/')} />
      </Screen>
    );
  }

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Abrir expediente' }} />
      {current.data ? <Banner tone="info" text="Já existe um expediente aberto." /> : null}
      <MoneyInput label="Saldo inicial do caixa (dinheiro na gaveta)" value={balance} onChange={setBalance} autoFocus />
      {open.error ? <Banner tone="danger" text={open.error} /> : null}
      {open.state === 'unknown' ? <Banner tone="warning" text="Não sabemos se o expediente foi aberto. Verificando…" /> : null}
      <Button
        title="Abrir"
        disabled={!canOpen || !online || !!current.data || open.state === 'unknown'}
        onPress={() => setConfirm({ confirmSameDate: false })}
      />
      {!online ? <Text muted>Sem conexão: abrir expediente exige internet.</Text> : null}
      <ConfirmDialog
        visible={confirm !== null}
        title={confirm?.confirmSameDate ? 'Já houve um expediente hoje' : 'Abrir expediente?'}
        description={confirm?.confirmSameDate ? 'Já existe um expediente fechado nesta data. Abrir outro mesmo assim?' : undefined}
        lines={[{ label: 'Saldo inicial', value: formatBRL(balance) }]}
        confirmLabel={confirm?.confirmSameDate ? 'Abrir outro expediente' : `Abrir com ${formatBRL(balance)}`}
        loading={open.busy}
        onCancel={() => setConfirm(null)}
        onConfirm={() => void open.run({ openingBalance: balance, confirmSameDate: confirm!.confirmSameDate })}
      />
    </Screen>
  );
}
