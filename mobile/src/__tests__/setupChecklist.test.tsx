/** 08 §8.9 Primeiro uso — "Prepare seu restaurante". */
import { render, screen } from '@testing-library/react-native';

import type { Me, MarmitaSize, Product } from '@/api/types';
import { SetupChecklist, setupComplete, setupState } from '@/features/onboarding/SetupChecklist';
import { useSession } from '@/stores/session';

jest.mock('expo-router', () => ({ router: { push: jest.fn() } }));

const size = (isActive: boolean) => ({ id: 's', isActive }) as MarmitaSize;
const product = (kind: Product['kind'], isActive = true) => ({ id: kind, kind, isActive }) as Product;

describe('setupState', () => {
  it('precisa de tamanho ativo e de prato principal ativo', () => {
    expect(setupComplete(setupState([], [product('MAIN_DISH')]))).toBe(false);
    expect(setupComplete(setupState([size(false)], [product('MAIN_DISH')]))).toBe(false);
    expect(setupComplete(setupState([size(true)], [product('SIDE_DISH')]))).toBe(false);
    expect(setupComplete(setupState([size(true)], [product('MAIN_DISH', false)]))).toBe(false);
    expect(setupComplete(setupState([size(true)], [product('MAIN_DISH')]))).toBe(true);
  });
});

describe('SetupChecklist', () => {
  it('marca o que está feito e só libera abrir o expediente com o cadastro completo', async () => {
    useSession.setState({ me: { permissions: ['catalog.manage', 'settings.manage', 'cash.open'], subscription: { canWrite: true } } as unknown as Me });
    await render(<SetupChecklist state={{ hasSizes: false, hasMainDishes: true }} />);
    expect(screen.getByText('Prepare seu restaurante')).toBeTruthy();
    expect(screen.getByLabelText('Pratos principais, feito')).toBeTruthy();
    expect(screen.getByLabelText('Tamanhos de marmita')).toBeTruthy();
    expect(screen.getByLabelText('Abrir o expediente').props.accessibilityState.disabled).toBe(true);
    expect(screen.getByText(/O preço da marmita vem do tamanho/)).toBeTruthy();
  });

  it('sem cash.open e settings.manage, não mostra esses passos', async () => {
    useSession.setState({ me: { permissions: ['catalog.manage'], subscription: { canWrite: true } } as unknown as Me });
    await render(<SetupChecklist state={{ hasSizes: false, hasMainDishes: false }} />);
    expect(screen.queryByText('Abrir o expediente')).toBeNull();
    expect(screen.queryByText('Formas de pagamento')).toBeNull();
  });
});
