/** 08 §8.9 Primeiro uso — "Prepare seu restaurante" (docs/10-ux.md D1, D2). Só leituras que o app já faz. */
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';
import { Pressable, View } from 'react-native';

import type { MarmitaSize, Product } from '@/api/types';
import { Card, Text, TOUCH, radius, space, useColors } from '@/components/ui';
import { usePermission } from '@/stores/session';

export interface SetupState {
  hasSizes: boolean;
  hasMainDishes: boolean;
}

export function setupState(sizes: MarmitaSize[], products: Product[]): SetupState {
  return {
    hasSizes: sizes.some((s) => s.isActive),
    hasMainDishes: products.some((p) => p.kind === 'MAIN_DISH' && p.isActive),
  };
}

export const setupComplete = (s: SetupState) => s.hasSizes && s.hasMainDishes;

interface Step {
  title: string;
  text: string;
  /** null = só conferir, sem marca de feito. */
  done: boolean | null;
  enabled: boolean;
  href: string;
}

export function SetupChecklist({ state }: { state: SetupState }) {
  const c = useColors();
  const canSettings = usePermission('settings.manage');
  const canOpen = usePermission('cash.open');
  const steps: Step[] = [
    { title: 'Tamanhos de marmita', text: 'O preço da marmita vem do tamanho: P, M, G.', done: state.hasSizes, enabled: true, href: '/menu/sizes' },
    { title: 'Pratos principais', text: 'Frango, bife, linguiça… Acompanhamentos e bebidas também ficam aqui.', done: state.hasMainDishes, enabled: true, href: '/menu/products' },
    ...(canSettings ? [{ title: 'Formas de pagamento', text: 'Confira se dinheiro, Pix e cartão estão como você usa.', done: null, enabled: true, href: '/menu/payment-methods' }] : []),
    ...(canOpen ? [{ title: 'Abrir o expediente', text: 'Depois de abrir, o app pede o cardápio de hoje.', done: false, enabled: setupComplete(state), href: '/business-day/open' }] : []),
  ];
  const doneCount = [state.hasSizes, state.hasMainDishes].filter(Boolean).length;
  const current = steps.findIndex((s) => s.done === false && s.enabled);

  return (
    <Card style={{ gap: space.md }}>
      <View style={{ gap: space.xs }}>
        <Text variant="big">Prepare seu restaurante</Text>
        <Text muted>{doneCount} de 2 cadastros prontos para vender a primeira marmita.</Text>
      </View>
      {steps.map((s, i) => {
        const active = i === current;
        return (
          <Pressable
            key={s.href}
            disabled={!s.enabled}
            onPress={() => router.push(s.href as never)}
            accessibilityRole="button"
            accessibilityLabel={`${s.title}${s.done ? ', feito' : ''}`}
            accessibilityState={{ disabled: !s.enabled }}
            style={({ pressed }) => ({
              flexDirection: 'row',
              alignItems: 'center',
              gap: space.md,
              minHeight: TOUCH + space.lg,
              padding: space.md,
              borderRadius: radius.md,
              borderWidth: active ? 2 : 1,
              borderColor: active ? c.primary : c.border,
              backgroundColor: s.done ? c.successBg : c.surface,
              opacity: !s.enabled ? 0.5 : pressed ? 0.7 : 1,
            })}
          >
            <View style={{ width: 32, height: 32, borderRadius: 16, alignItems: 'center', justifyContent: 'center', backgroundColor: s.done ? c.success : active ? c.primary : c.surfaceAlt }}>
              {s.done ? <Ionicons name="checkmark" size={20} color="#FFFFFF" /> : <Text style={{ fontWeight: '700', color: active ? c.primaryText : c.textMuted }}>{i + 1}</Text>}
            </View>
            <View style={{ flex: 1, gap: 2 }}>
              <Text style={{ fontWeight: '700', color: s.done ? c.success : c.text }}>{s.title}</Text>
              <Text variant="small" muted>
                {s.text}
              </Text>
            </View>
            {s.done ? null : <Ionicons name="chevron-forward" size={20} color={c.textMuted} />}
          </Pressable>
        );
      })}
    </Card>
  );
}
