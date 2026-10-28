import { ApiError, errorMessage, NetworkUnknownError } from '@/api/errors';
import { keysForEntity } from '@/api/queryKeys';

describe('ProblemDetails (06 §6.1)', () => {
  it('separa os campos padrão das extensões', () => {
    const e = new ApiError(409, {
      type: 'urn:almoseis:error:version-conflict',
      title: 'O registro foi alterado por outra pessoa.',
      status: 409,
      errorCode: 'VersionConflict',
      traceId: '00-9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d-9a8b7c6d5e4f3a2b-01',
      expectedVersion: 4,
      currentVersion: 5,
      current: { id: 'x', version: 5 },
    });
    expect(e.errorCode).toBe('VersionConflict');
    expect(e.extensions.current).toEqual({ id: 'x', version: 5 });
    expect(e.extensions.currentVersion).toBe(5);
    expect(e.extensions).not.toHaveProperty('title');
  });

  it('mensagens por errorCode (08 §8.11)', () => {
    expect(errorMessage(new ApiError(409, { errorCode: 'OrderAlreadyPaid' }))).toBe('Este pedido já foi quitado. Para corrigir, estorne um pagamento.');
    expect(errorMessage(new ApiError(422, { errorCode: 'PaymentExceedsBalance', balance: '35.00' }))).toBe('O valor passa do saldo (R$ 35,00).');
    expect(errorMessage(new ApiError(422, { errorCode: 'ProductUnavailable', detail: 'Costela não está disponível no cardápio de hoje.' }))).toBe(
      'Costela não está disponível no cardápio de hoje.',
    );
    expect(errorMessage(new ApiError(429, { errorCode: 'TooManyRequests' }, 30))).toBe('Muitas tentativas. Aguarde 30 s.');
    expect(errorMessage(new ApiError(422, { errorCode: 'ValidationFailed', errors: { 'items[0].quantity': ['Deve ser entre 1 e 99.'] } }))).toBe(
      'Deve ser entre 1 e 99.',
    );
    expect(errorMessage(new ApiError(401, { errorCode: 'InvalidCredentials', attemptsLeft: 2 }))).toBe('PIN incorreto. Restam 2 tentativas.');
    expect(errorMessage(new ApiError(500, { traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01' }))).toBe('Algo deu errado. Código: 4bf92f35.');
    expect(errorMessage(new NetworkUnknownError())).toBe('Sem conexão com o servidor.');
  });

  it('campo de validação sem diferenciar maiúsculas', () => {
    const e = new ApiError(422, { errorCode: 'ValidationFailed', errors: { ImageStorageKey: ['A imagem não pertence a este produto.'] } });
    expect(e.fieldError('imageStorageKey')).toBe('A imagem não pertence a este produto.');
  });
});

describe('mapa entityType → invalidação (08 §8.3)', () => {
  it('ORDER invalida o pedido, listas, produção e clientes', () => {
    expect(keysForEntity('ORDER', 'o1')).toEqual([['orders', 'o1'], ['orders', 'list'], ['production'], ['customers']]);
  });

  it('PAYMENT invalida pedidos e o resumo do fechamento', () => {
    expect(keysForEntity('PAYMENT_REVERSAL', 'p1')).toEqual([['orders'], ['closing', 'summary']]);
  });

  it('EMPLOYEE só invalida ["me"] quando é o próprio', () => {
    expect(keysForEntity('EMPLOYEE', 'e1', 'e1')).toEqual([['me'], ['employees']]);
    expect(keysForEntity('EMPLOYEE', 'e2', 'e1')).toEqual([['employees']]);
  });

  it('caixa e fechamento', () => {
    for (const t of ['BUSINESS_DAY', 'CASH_MOVEMENT', 'DAILY_CLOSING', 'CLOSING_ADJUSTMENT'] as const) {
      expect(keysForEntity(t, 'x')).toEqual([['businessDay'], ['closing']]);
    }
    expect(keysForEntity('TENANT_SETTINGS')).toEqual([['products'], ['marmitaSizes'], ['settings']]);
  });
});
