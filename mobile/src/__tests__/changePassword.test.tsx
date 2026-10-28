/** 08 §8.10 Perfil: troca de senha (`PUT /auth/me/password`). */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, userEvent, waitFor } from '@testing-library/react-native';

import { authApi } from '@/api/endpoints/auth';
import { ApiError } from '@/api/errors';
import { ChangePasswordSection } from '@/features/settings/ChangePasswordSection';

jest.mock('@/api/endpoints/auth', () => ({ authApi: { changePassword: jest.fn() } }));

const changePassword = authApi.changePassword as jest.Mock;

async function renderSection() {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  await render(
    <QueryClientProvider client={client}>
      <ChangePasswordSection />
    </QueryClientProvider>,
  );
}

async function fill(current: string, next: string, confirm: string) {
  await fireEvent.changeText(screen.getByTestId('current-password'), current);
  await fireEvent.changeText(screen.getByTestId('new-password'), next);
  await fireEvent.changeText(screen.getByTestId('confirm-password'), confirm);
}

beforeEach(() => changePassword.mockReset());

describe('ChangePasswordSection', () => {
  it('só envia com senha atual, nova de 8+ caracteres, diferente da atual e confirmada', async () => {
    await renderSection();
    const user = userEvent.setup();

    await fill('S3nha-forte!', 'curta', 'curta');
    expect(screen.getByText('A senha precisa de pelo menos 8 caracteres.')).toBeTruthy();
    await user.press(screen.getByTestId('change-password'));

    await fill('S3nha-forte!', 'Outra-s3nha!', 'Outra-s3nhA');
    expect(screen.getByText('As senhas não conferem.')).toBeTruthy();
    await user.press(screen.getByTestId('change-password'));

    await fill('S3nha-forte!', 'S3nha-forte!', 'S3nha-forte!');
    expect(screen.getByText('A nova senha precisa ser diferente da atual.')).toBeTruthy();
    await user.press(screen.getByTestId('change-password'));

    expect(changePassword).not.toHaveBeenCalled();

    changePassword.mockResolvedValue({ data: undefined });
    await fill('S3nha-forte!', 'Outra-s3nha!', 'Outra-s3nha!');
    await user.press(screen.getByTestId('change-password'));
    await waitFor(() => expect(changePassword).toHaveBeenCalledWith('S3nha-forte!', 'Outra-s3nha!'));
    await waitFor(() => expect(screen.getByTestId('current-password').props.value).toBe(''));
  });

  it('senha atual errada (422) aparece no campo', async () => {
    changePassword.mockRejectedValue(
      new ApiError(422, { errorCode: 'ValidationFailed', errors: { currentPassword: ['Senha atual incorreta.'] } }),
    );
    await renderSection();
    await fill('errada', 'Outra-s3nha!', 'Outra-s3nha!');
    await userEvent.setup().press(screen.getByTestId('change-password'));
    expect(await screen.findByText('Senha atual incorreta.')).toBeTruthy();
    expect(screen.getByTestId('current-password').props.value).toBe('errada');
  });
});
