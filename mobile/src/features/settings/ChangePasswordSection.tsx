/** 08 §8.10 Perfil: troca da própria senha (`PUT /auth/me/password`). */
import { useState } from 'react';

import { ApiError, errorMessage } from '@/api/errors';
import { Banner, Button, Section, Text, TextField } from '@/components/ui';
import { useChangePassword } from '@/features/session/hooks';
import { toast } from '@/stores/toast';

const MIN_LENGTH = 8;

export function ChangePasswordSection() {
  const change = useChangePassword();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');

  const tooShort = next.length > 0 && next.length < MIN_LENGTH;
  const mismatch = confirm.length > 0 && confirm !== next;
  const sameAsCurrent = next.length > 0 && next === current;
  const valid = !!current && next.length >= MIN_LENGTH && confirm === next && !sameAsCurrent;

  // 422 com campo vira erro embaixo do campo; o resto vai para o banner.
  const fieldError = (field: string) => (change.error instanceof ApiError ? change.error.fieldError(field) : undefined);
  const currentError = fieldError('currentPassword');
  const newError = fieldError('newPassword');
  const bannerError = change.error && !currentError && !newError ? errorMessage(change.error) : null;

  const submit = () =>
    change.mutate(
      { currentPassword: current, newPassword: next },
      {
        onSuccess: () => {
          setCurrent('');
          setNext('');
          setConfirm('');
          toast('Senha trocada. Os outros aparelhos vão pedir login de novo.');
        },
      },
    );

  return (
    <Section title="Trocar senha">
      <Text muted>Ao trocar, os outros aparelhos em que você entrou com senha vão pedir login de novo. Este continua conectado.</Text>
      <TextField
        testID="current-password"
        label="Senha atual"
        value={current}
        onChangeText={setCurrent}
        secureTextEntry
        autoComplete="current-password"
        error={currentError}
      />
      <TextField
        testID="new-password"
        label="Nova senha"
        value={next}
        onChangeText={setNext}
        secureTextEntry
        autoComplete="new-password"
        hint="Pelo menos 8 caracteres."
        error={newError ?? (tooShort ? 'A senha precisa de pelo menos 8 caracteres.' : sameAsCurrent ? 'A nova senha precisa ser diferente da atual.' : null)}
      />
      <TextField
        testID="confirm-password"
        label="Repita a nova senha"
        value={confirm}
        onChangeText={setConfirm}
        secureTextEntry
        autoComplete="new-password"
        error={mismatch ? 'As senhas não conferem.' : null}
      />
      {bannerError ? <Banner tone="danger" text={bannerError} /> : null}
      <Button testID="change-password" title="Trocar senha" disabled={!valid} loading={change.isPending} onPress={submit} />
    </Section>
  );
}
