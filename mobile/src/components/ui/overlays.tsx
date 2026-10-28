/** Diálogos e folhas (08 §8.11): ConfirmDialog, BottomSheet, ConflictSheet, Toaster. */
import type { ReactNode } from 'react';
import { KeyboardAvoidingView, Modal, Platform, Pressable, ScrollView, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { useToast } from '@/stores/toast';

import { Button, Text } from './primitives';
import { radius, space, useColors } from './theme';

export interface ConfirmLine {
  label: string;
  value: string;
}

/**
 * RF-012: título com o verbo, "o que vai acontecer" e valores; o botão repete o verbo ("Receber R$ 35,00"),
 * nunca "OK". Cancelar não grava nada (RN-012.4).
 */
export function ConfirmDialog({
  visible,
  title,
  lines = [],
  description,
  warning,
  confirmLabel,
  danger,
  loading,
  onConfirm,
  onCancel,
  children,
}: {
  visible: boolean;
  title: string;
  lines?: ConfirmLine[];
  description?: string;
  warning?: string | null;
  confirmLabel: string;
  danger?: boolean;
  loading?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  children?: ReactNode;
}) {
  const c = useColors();
  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onCancel}>
      <View style={{ flex: 1, backgroundColor: c.overlay, justifyContent: 'center', padding: space.lg }}>
        <View accessibilityViewIsModal style={{ backgroundColor: c.surface, borderRadius: radius.lg, padding: space.lg, gap: space.md, maxHeight: '85%' }}>
          <Text variant="title">{title}</Text>
          <ScrollView contentContainerStyle={{ gap: space.sm }}>
            {description ? <Text>{description}</Text> : null}
            {lines.map((l) => (
              <View key={l.label} style={{ flexDirection: 'row', justifyContent: 'space-between', gap: space.md }}>
                <Text muted style={{ flexShrink: 1 }}>
                  {l.label}
                </Text>
                <Text style={{ fontWeight: '700', flexShrink: 1, textAlign: 'right' }}>{l.value}</Text>
              </View>
            ))}
            {warning ? <Text style={{ color: c.warning, fontWeight: '600' }}>{warning}</Text> : null}
            {children}
          </ScrollView>
          <View style={{ gap: space.sm }}>
            <Button testID="confirm-button" title={confirmLabel} variant={danger ? 'danger' : 'primary'} loading={loading} onPress={onConfirm} />
            <Button testID="cancel-button" title="Voltar" variant="secondary" disabled={loading} onPress={onCancel} />
          </View>
        </View>
      </View>
    </Modal>
  );
}

export function BottomSheet({ visible, title, onClose, children, footer }: { visible: boolean; title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  const c = useColors();
  const insets = useSafeAreaInsets();
  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose}>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{ flex: 1, backgroundColor: c.overlay, justifyContent: 'flex-end' }}>
        <Pressable style={{ flex: 1 }} onPress={onClose} accessibilityLabel="Fechar" />
        <View style={{ backgroundColor: c.bg, borderTopLeftRadius: radius.lg, borderTopRightRadius: radius.lg, maxHeight: '90%', paddingBottom: insets.bottom }}>
          <View style={{ padding: space.lg, paddingBottom: space.sm, flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' }}>
            <Text variant="title">{title}</Text>
            <Button title="Fechar" variant="ghost" onPress={onClose} />
          </View>
          <ScrollView contentContainerStyle={{ padding: space.lg, paddingTop: 0, gap: space.md }} keyboardShouldPersistTaps="handled">
            {children}
          </ScrollView>
          {footer ? <View style={{ padding: space.lg, gap: space.sm }}>{footer}</View> : null}
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

/** 409 VersionConflict (ADR-0034): mostra o estado atual e pede para refazer. Nunca sobrescreve. */
export function ConflictSheet({ visible, summary, onRedo, onClose }: { visible: boolean; summary: string[]; onRedo: () => void; onClose: () => void }) {
  return (
    <BottomSheet visible={visible} title="Alterado por outra pessoa" onClose={onClose} footer={<Button title="Refazer sobre a versão atual" onPress={onRedo} />}>
      <Text>Enquanto você editava, este registro mudou. Nada foi gravado. Confira como está agora:</Text>
      {summary.map((line) => (
        <Text key={line}>• {line}</Text>
      ))}
    </BottomSheet>
  );
}

export function Toaster() {
  const c = useColors();
  const toasts = useToast((s) => s.toasts);
  const dismiss = useToast((s) => s.dismiss);
  const insets = useSafeAreaInsets();
  if (toasts.length === 0) return null;
  return (
    <View pointerEvents="box-none" style={{ position: 'absolute', left: space.lg, right: space.lg, bottom: insets.bottom + 80, gap: space.sm }}>
      {toasts.map((t) => (
        <Pressable
          key={t.id}
          onPress={() => dismiss(t.id)}
          accessibilityRole="alert"
          style={{ backgroundColor: t.tone === 'error' ? c.danger : t.tone === 'info' ? c.info : c.text, padding: space.md, borderRadius: radius.md }}
        >
          <Text style={{ color: c.bg, fontWeight: '600' }}>{t.message}</Text>
        </Pressable>
      ))}
    </View>
  );
}
