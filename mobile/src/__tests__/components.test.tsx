/** 08 §8.13: ConfirmDialog, MoneyInput, PermissionGate e OrderCard com os dois selos. */
import { fireEvent, render, screen, userEvent } from '@testing-library/react-native';
import { useState } from 'react';

import type { Me, OrderSummary } from '@/api/types';
import { Button, ConfirmDialog, MoneyInput, PaymentBadge, PermissionGate, ProductionBadge, Text } from '@/components/ui';
import { OrderCard } from '@/features/orders/components';
import { useSession } from '@/stores/session';

jest.mock('expo-router', () => ({ router: { push: jest.fn() } }));

describe('ConfirmDialog (RF-012)', () => {
  it('cancelar não confirma; o botão repete o verbo', async () => {
    const onConfirm = jest.fn();
    const onCancel = jest.fn();
    await render(
      <ConfirmDialog visible title="Receber pagamento?" lines={[{ label: 'Valor', value: 'R$ 35,00' }]} confirmLabel="Receber R$ 35,00" onConfirm={onConfirm} onCancel={onCancel} />,
    );
    expect(screen.getByText('R$ 35,00')).toBeTruthy();
    expect(screen.queryByText('OK')).toBeNull();
    await fireEvent.press(screen.getByTestId('cancel-button'));
    expect(onCancel).toHaveBeenCalled();
    expect(onConfirm).not.toHaveBeenCalled();
    await fireEvent.press(screen.getByText('Receber R$ 35,00'));
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  it('com loading não aceita toque duplo', async () => {
    const onConfirm = jest.fn();
    await render(<ConfirmDialog visible title="Receber?" confirmLabel="Receber" loading onConfirm={onConfirm} onCancel={() => {}} />);
    await userEvent.setup().press(screen.getByTestId('confirm-button'));
    expect(onConfirm).not.toHaveBeenCalled();
  });
});

describe('MoneyInput', () => {
  function Harness() {
    const [v, setV] = useState('0.00');
    return (
      <>
        <MoneyInput testID="money" label="Valor" value={v} onChange={setV} />
        <Text testID="raw">{v}</Text>
      </>
    );
  }

  it('digitação da direita para a esquerda devolve MoneyString', async () => {
    await render(<Harness />);
    await fireEvent.changeText(screen.getByTestId('money'), '3550');
    expect(screen.getByTestId('raw').props.children).toBe('35.50');
    expect(screen.getByTestId('money').props.value).toBe('R$ 35,50');
  });
});

const me = (permissions: Me['permissions']): Me =>
  ({
    user: { id: 'u', fullName: 'Ana', email: null, phoneNumber: null },
    tenant: { id: 't', name: 'Marmitaria', timeZone: 'America/Sao_Paulo' },
    employee: { id: 'e', displayName: 'Ana', roles: ['OWNER'], hasPin: false },
    permissions,
    memberships: [],
    subscription: { status: null, trialEndsAt: null, daysLeft: null, canWrite: true },
    sharedDevice: null,
    serverTime: '',
  }) as Me;

describe('PermissionGate', () => {
  it('esconde sem a permissão e mostra com ela', async () => {
    useSession.setState({ me: me(['orders.view']) });
    const first = await render(
      <PermissionGate permission="payments.register">
        <Text>Receber</Text>
      </PermissionGate>,
    );
    expect(screen.queryByText('Receber')).toBeNull();
    await first.unmount();

    useSession.setState({ me: me(['payments.register']) });
    await render(
      <PermissionGate permission="payments.register">
        <Text>Receber</Text>
      </PermissionGate>,
    );
    expect(screen.getByText('Receber')).toBeTruthy();
  });

  it('fallback="disabled" mostra desabilitado com a explicação', async () => {
    useSession.setState({ me: me([]) });
    const onPress = jest.fn();
    await render(
      <PermissionGate permission="orders.cancel" fallback="disabled">
        <Button title="Cancelar pedido" onPress={onPress} />
      </PermissionGate>,
    );
    await userEvent.setup().press(screen.getByText('Cancelar pedido'));
    expect(onPress).not.toHaveBeenCalled();
    expect(screen.getByText(/não tem permissão/)).toBeTruthy();
  });
});

describe('selos (RNF-001)', () => {
  it('produção é pílula e financeiro é quadrado, com textos distintos', async () => {
    await render(
      <>
        <ProductionBadge status="IN_PROGRESS" />
        <PaymentBadge totalAmount="55.00" paidAmount="20.00" isPaid={false} />
      </>,
    );
    const production = screen.getByTestId('production-badge');
    const payment = screen.getByTestId('payment-badge');
    expect(production.props.style.borderRadius).toBe(999);
    expect(payment.props.style.borderRadius).toBe(3);
    expect(screen.getByText('Em produção')).toBeTruthy();
    expect(screen.getByText('R$ 20 de R$ 55')).toBeTruthy();
  });

  it('OrderCard mostra os dois selos', async () => {
    useSession.setState({ me: me(['orders.view']) });
    const order: OrderSummary = {
      id: 'o1',
      version: 4,
      orderNumber: 12,
      businessDate: '2026-10-27',
      orderType: 'MARMITA',
      fulfillmentType: 'DELIVERY',
      customerName: 'João Silva',
      tableLabel: null,
      tabNumber: null,
      totalAmount: '55.00',
      paidAmount: '0.00',
      balance: '55.00',
      isPaid: false,
      productionStatus: 'OPEN',
      cancellationStatus: 'ACTIVE',
      itemCount: 3,
      pendingAlertCount: 1,
      createdAt: '2026-10-27T14:30:00Z',
    };
    await render(<OrderCard order={order} />);
    expect(screen.getByText('#12')).toBeTruthy();
    expect(screen.getByText('João Silva')).toBeTruthy();
    expect(screen.getByTestId('production-badge')).toBeTruthy();
    expect(screen.getByTestId('payment-badge')).toBeTruthy();
    expect(screen.getByText('A pagar')).toBeTruthy();
    expect(screen.getByLabelText('1 alteração não vista')).toBeTruthy();
  });
});
