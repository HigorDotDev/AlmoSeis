/** Partes comuns dos formulários do cardápio fixo: carregamento, rodapé (salvar, inativar, reativar), linhas. */
import { Stack } from 'expo-router';
import type { ReactNode } from 'react';
import { View } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import type { FixedMenu } from '@/api/types';
import { Banner, Button, ErrorState, IconButton, Row, Screen, Skeleton, Text, TextField, useColors } from '@/components/ui';
import { confirmAction } from '@/lib/confirm';

import { fixedMenuErrorText } from './components';
import { useFixedMenu } from './hooks';

/** Carrega o cardápio (com inativos) e entrega para o formulário; sem a raiz pedida, mostra o erro. */
export function WithFixedMenu({ title, children }: { title: string; children: (menu: FixedMenu) => ReactNode }) {
  const menu = useFixedMenu(true);
  if (menu.isLoading) {
    return (
      <Screen>
        <Stack.Screen options={{ title }} />
        <Skeleton lines={5} />
      </Screen>
    );
  }
  if (!menu.data) {
    return (
      <Screen>
        <Stack.Screen options={{ title }} />
        <ErrorState message={errorMessage(menu.error)} onRetry={() => void menu.refetch()} />
      </Screen>
    );
  }
  return <>{children(menu.data)}</>;
}

export function NotFound({ title }: { title: string }) {
  return (
    <Screen>
      <Stack.Screen options={{ title }} />
      <ErrorState message="Este item não existe mais. Volte e atualize o cardápio." />
    </Screen>
  );
}

/** Erro da última gravação: `409` de versão (o formulário já voltou com o que está salvo), `UsedInActiveCombos`, `422`… */
export function SaveError({ error }: { error: unknown }) {
  if (!error) return null;
  return <Banner tone={isApiError(error, 'VersionConflict') ? 'warning' : 'danger'} text={fixedMenuErrorText(error)} />;
}

export function FieldError({ text }: { text?: string }) {
  const c = useColors();
  if (!text) return null;
  return (
    <Text variant="small" style={{ color: c.danger }}>
      {text}
    </Text>
  );
}

export function SortOrderField({ value, onChange, editable, error, label = 'Ordem' }: { value: string; onChange: (v: string) => void; editable: boolean; error?: string; label?: string }) {
  return <TextField label={label} value={value} onChangeText={(t) => onChange(t.replace(/\D/g, ''))} keyboardType="number-pad" editable={editable} error={error} />;
}

/** Subir, descer e remover uma linha (tamanho, sabor, opção, componente). */
export function RowActions({ name, index, count, onMove, onRemove, editable }: { name: string; index: number; count: number; onMove: (d: -1 | 1) => void; onRemove: () => void; editable: boolean }) {
  if (!editable) return null;
  return (
    <Row gap={0} style={{ justifyContent: 'flex-end' }}>
      <IconButton icon="arrow-up" label={`Subir ${name}`} disabled={index === 0} onPress={() => onMove(-1)} />
      <IconButton icon="arrow-down" label={`Descer ${name}`} disabled={index === count - 1} onPress={() => onMove(1)} />
      <IconButton icon="trash-outline" label={`Remover ${name}`} onPress={onRemove} />
    </Row>
  );
}

/**
 * Rodapé: "Salvar" e, na edição, "Excluir (inativar)" com confirmação ou "Reativar". Itens vendidos ou usados em combo
 * nunca são apagados (ADR-0056 item 26).
 */
export function FormFooter({
  canManage,
  saving,
  onSave,
  existing,
  isActive,
  itemName,
  deactivateText,
  onDeactivate,
  onReactivate,
}: {
  canManage: boolean;
  saving: boolean;
  onSave: () => void;
  existing: boolean;
  isActive: boolean;
  itemName: string;
  deactivateText: string;
  onDeactivate: () => void;
  onReactivate: () => void;
}) {
  if (!canManage) return null;
  return (
    <View style={{ gap: 8 }}>
      <Button title="Salvar" size="large" loading={saving} onPress={onSave} />
      {existing && isActive ? (
        <Button
          title="Excluir (inativar)"
          variant="danger"
          disabled={saving}
          onPress={() => confirmAction({ title: `Inativar “${itemName}”?`, message: deactivateText, confirmText: 'Inativar', onConfirm: onDeactivate })}
        />
      ) : null}
      {existing && !isActive ? <Button title="Reativar" variant="secondary" disabled={saving} onPress={onReactivate} /> : null}
    </View>
  );
}
