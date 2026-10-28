/** 08 §8.9 Nova marmita: sem tamanho cadastrado o app explica em vez de só travar o botão. */
import { fireEvent, render, screen } from '@testing-library/react-native';
import { router } from 'expo-router';

import type { Me } from '@/api/types';
import { MarmitaBuilderSheet } from '@/features/orders/MarmitaBuilderSheet';
import { useSession } from '@/stores/session';

jest.mock('expo-router', () => ({ router: { push: jest.fn() } }));

function signIn(permissions: Me['permissions']) {
  useSession.setState({ me: { permissions, subscription: { canWrite: true } } as unknown as Me });
}

async function renderSheet(onClose = jest.fn()) {
  await render(<MarmitaBuilderSheet visible initial={null} sizes={[]} menu={null} addons={[]} onSave={jest.fn()} onClose={onClose} />);
  return onClose;
}

describe('MarmitaBuilderSheet sem tamanhos', () => {
  it('quem gerencia o catálogo recebe o atalho para cadastrar', async () => {
    signIn(['orders.view', 'orders.create', 'catalog.manage']);
    const onClose = await renderSheet();
    expect(screen.getByText(/Nenhum tamanho de marmita cadastrado/)).toBeTruthy();
    await fireEvent.press(screen.getByText('Cadastrar tamanhos'));
    expect(onClose).toHaveBeenCalled();
    expect(router.push).toHaveBeenCalledWith('/menu/sizes');
  });

  it('sem permissão, só orienta a pedir o cadastro', async () => {
    signIn(['orders.view', 'orders.create']);
    await renderSheet();
    expect(screen.getByText(/Peça ao proprietário ou gerente/)).toBeTruthy();
    expect(screen.queryByText('Cadastrar tamanhos')).toBeNull();
  });
});
