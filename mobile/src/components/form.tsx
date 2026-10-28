/** Campos ligados ao React Hook Form (docs/02-padroes.md §2.3: RHF + Zod). */
import type { Control, FieldPath, FieldValues } from 'react-hook-form';
import { Controller } from 'react-hook-form';
import type { TextInputProps } from 'react-native';

import type { ApiError } from '@/api/errors';
import { MoneyInput, TextField } from '@/components/ui';

export function FormTextField<T extends FieldValues>({
  control,
  name,
  label,
  serverError,
  format,
  ...props
}: Omit<TextInputProps, 'value' | 'onChangeText'> & {
  control: Control<T>;
  name: FieldPath<T>;
  label: string;
  serverError?: string;
  /** Máscara aplicada ao digitar (telefone, documento). */
  format?: (text: string) => string;
}) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <TextField
          {...props}
          label={label}
          value={(field.value as string | null) ?? ''}
          onChangeText={(t) => field.onChange(format ? format(t) : t)}
          onBlur={field.onBlur}
          error={fieldState.error?.message ?? serverError}
        />
      )}
    />
  );
}

export function FormMoneyField<T extends FieldValues>({ control, name, label, serverError }: { control: Control<T>; name: FieldPath<T>; label: string; serverError?: string }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <MoneyInput label={label} value={(field.value as string | null) ?? '0.00'} onChange={field.onChange} error={fieldState.error?.message ?? serverError} />
      )}
    />
  );
}

/** Erro do servidor para um campo (422 `errors`), procurando sem diferenciar maiúsculas. */
export function serverFieldError(error: unknown, field: string): string | undefined {
  const e = error as ApiError | null;
  return e && typeof e.fieldError === 'function' ? e.fieldError(field) : undefined;
}
