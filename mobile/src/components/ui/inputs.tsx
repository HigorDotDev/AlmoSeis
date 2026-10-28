/** Entradas de domínio (08 §8.11): dinheiro em centavos, peso em gramas, quantidade. */
import { Ionicons } from '@expo/vector-icons';
import { useState } from 'react';
import { Pressable, TextInput, View } from 'react-native';

import { digitsToCents, formatBRL, fromCents, toCents, type MoneyString } from '@/lib/money';
import { gramsToKgLabel, kgInputToGrams, MAX_WEIGHT_GRAMS } from '@/lib/weight';

import { Text } from './primitives';
import { font, radius, space, TOUCH, useColors } from './theme';

export function MoneyText({ value, style, strong }: { value: MoneyString; style?: object; strong?: boolean }) {
  return <Text style={[strong ? { fontWeight: '700' } : null, style]}>{formatBRL(value)}</Text>;
}

/**
 * Teclado numérico, digitação da direita para a esquerda em centavos ("3550" → R$ 35,50). Devolve `MoneyString`.
 */
export function MoneyInput({
  label,
  value,
  onChange,
  error,
  autoFocus,
  testID,
  allowZero = true,
}: {
  label: string;
  value: MoneyString | null;
  onChange: (value: MoneyString) => void;
  error?: string | null;
  autoFocus?: boolean;
  testID?: string;
  allowZero?: boolean;
}) {
  const c = useColors();
  const cents = value === null ? 0 : toCents(value);
  return (
    <View style={{ gap: space.xs }}>
      <Text variant="small" muted>
        {label}
      </Text>
      <TextInput
        testID={testID}
        accessibilityLabel={label}
        keyboardType="number-pad"
        autoFocus={autoFocus}
        value={cents === 0 && !allowZero ? '' : formatBRL(cents)}
        onChangeText={(text) => onChange(fromCents(digitsToCents(text)))}
        style={{
          minHeight: TOUCH + 8,
          borderWidth: 1,
          borderColor: error ? c.danger : c.border,
          borderRadius: radius.md,
          paddingHorizontal: space.md,
          color: c.text,
          backgroundColor: c.surface,
          fontSize: font.xl,
          fontWeight: '700',
        }}
      />
      {error ? (
        <Text variant="small" style={{ color: c.danger }}>
          {error}
        </Text>
      ) : null}
    </View>
  );
}

/** kg com 3 casas → gramas inteiras; limite de 50 kg. `onChange(null)` enquanto o texto não é um peso válido. */
export function WeightInput({ label, onChange, error, autoFocus }: { label: string; onChange: (grams: number | null) => void; error?: string | null; autoFocus?: boolean }) {
  const c = useColors();
  const [text, setText] = useState('');
  const grams = kgInputToGrams(text);
  const invalid = text !== '' && grams === null;
  return (
    <View style={{ gap: space.xs }}>
      <Text variant="small" muted>
        {label}
      </Text>
      <TextInput
        accessibilityLabel={label}
        keyboardType="decimal-pad"
        autoFocus={autoFocus}
        placeholder="0,000"
        placeholderTextColor={c.textMuted}
        value={text}
        onChangeText={(t) => {
          const clean = t.replace(/[^\d,.]/g, '');
          setText(clean);
          onChange(kgInputToGrams(clean));
        }}
        style={{
          minHeight: TOUCH + 8,
          borderWidth: 1,
          borderColor: error || invalid ? c.danger : c.border,
          borderRadius: radius.md,
          paddingHorizontal: space.md,
          color: c.text,
          backgroundColor: c.surface,
          fontSize: font.xl,
          fontWeight: '700',
        }}
      />
      <Text variant="small" style={{ color: error || invalid ? c.danger : c.textMuted }}>
        {error ?? (invalid ? `Peso entre 0,001 e ${gramsToKgLabel(MAX_WEIGHT_GRAMS)}.` : grams ? gramsToKgLabel(grams) : 'Em kg, com 3 casas: 0,452')}
      </Text>
    </View>
  );
}

export function QuantityStepper({ value, onChange, min = 0, max = 99, label }: { value: number; onChange: (v: number) => void; min?: number; max?: number; label: string }) {
  const c = useColors();
  const btn = (icon: 'remove' | 'add', next: number, disabled: boolean, a11y: string) => (
    <Pressable
      onPress={disabled ? undefined : () => onChange(next)}
      accessibilityRole="button"
      accessibilityLabel={a11y}
      accessibilityState={{ disabled }}
      style={{ width: TOUCH, height: TOUCH, borderRadius: radius.md, backgroundColor: c.surfaceAlt, alignItems: 'center', justifyContent: 'center', opacity: disabled ? 0.4 : 1 }}
    >
      <Ionicons name={icon} size={22} color={c.text} />
    </Pressable>
  );
  return (
    <View style={{ flexDirection: 'row', alignItems: 'center', gap: space.sm }} accessibilityLabel={`${label}: ${value}`}>
      {btn('remove', value - 1, value <= min, `Diminuir ${label}`)}
      <Text variant="subtitle" style={{ minWidth: 28, textAlign: 'center' }}>
        {value}
      </Text>
      {btn('add', value + 1, value >= max, `Aumentar ${label}`)}
    </View>
  );
}
