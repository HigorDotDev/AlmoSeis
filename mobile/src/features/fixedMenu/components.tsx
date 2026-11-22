/** Peças das telas do cardápio fixo (EP-003 fatia 2): "Vende no site", pausa, situação e foto. */
import { Image } from 'expo-image';
import * as ImagePicker from 'expo-image-picker';
import { Switch, View } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import type { Uuid } from '@/api/types';
import { Banner, Button, Card, Row, Text, radius, useColors } from '@/components/ui';
import { toast } from '@/stores/toast';

import { FIXED_MENU_CONFLICT_TEXT, type ItemStatus } from './fixedMenu';
import { usePauseFixedMenuItem, useUploadFixedMenuImage, type PauseRequest } from './hooks';

export { SiteSaleField } from '@/components/SiteSaleField';

export function ToggleRow({ label, hint, value, disabled, onChange }: { label: string; hint?: string; value: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
  return (
    <Row style={{ justifyContent: 'space-between', minHeight: 48 }}>
      <View style={{ flex: 1 }}>
        <Text style={{ fontWeight: '600' }}>{label}</Text>
        {hint ? (
          <Text variant="small" muted>
            {hint}
          </Text>
        ) : null}
      </View>
      <Switch value={value} onValueChange={onChange} disabled={disabled} accessibilityLabel={label} />
    </Row>
  );
}

export function StatusText({ status }: { status: ItemStatus | null }) {
  const c = useColors();
  if (!status) return null;
  const color = { danger: c.danger, warning: c.warning, neutral: c.textMuted }[status.tone];
  return (
    <Text variant="small" style={{ color, fontWeight: '700' }}>
      {status.text}
    </Text>
  );
}

/**
 * Chave de pausa (`catalog.availability`): ligada = disponível. Sem a permissão (ou em modo somente leitura),
 * mostra só o texto da situação.
 */
export function PauseSwitch({ name, paused, canPause, busy, onChange }: { name: string; paused: boolean; canPause: boolean; busy?: boolean; onChange: (paused: boolean) => void }) {
  const c = useColors();
  return (
    <Row gap={4}>
      <Text variant="small" style={{ color: paused ? c.warning : c.textMuted, fontWeight: paused ? '700' : '400' }}>
        {paused ? 'Pausado' : 'Disponível'}
      </Text>
      {canPause ? <Switch value={!paused} disabled={busy} accessibilityLabel={`${name} disponível`} onValueChange={(on) => onChange(!on)} /> : null}
    </Row>
  );
}

/** Pausar/reativar com aviso. `409` de versão: o cardápio já foi recarregado; a pessoa toca de novo. */
export function usePauseAction() {
  const pause = usePauseFixedMenuItem();
  const run = (r: PauseRequest) =>
    pause.mutate(r, {
      onSuccess: () => toast(r.paused ? `${r.label} pausado` : `${r.label} reativado`, 'success'),
      onError: (e) =>
        toast(isApiError(e, 'VersionConflict') ? 'O cardápio mudou em outro aparelho e foi atualizado. Confira e toque de novo.' : errorMessage(e), 'error'),
    });
  const busyId = pause.isPending ? (pause.variables?.id ?? null) : null;
  return { run, busyId };
}

export function fixedMenuErrorText(e: unknown): string {
  if (isApiError(e, 'VersionConflict')) return FIXED_MENU_CONFLICT_TEXT;
  return errorMessage(e);
}

/** Foto de prato ou combo, como a do produto (08 §8.10). Só depois de salvar (a chave leva o id). */
export function PhotoCard({
  kind,
  id,
  version,
  imageUrl,
  canManage,
  onRemove,
  onUploaded,
  onConflict,
}: {
  kind: 'dish' | 'combo';
  id: Uuid;
  version: number;
  imageUrl: string | null;
  canManage: boolean;
  /** `PATCH { imageStorageKey: null }` pela mutação da tela. */
  onRemove: () => void;
  /** Versão nova da raiz depois do `PATCH imageStorageKey`. */
  onUploaded: (version: number) => void;
  onConflict: () => void;
}) {
  const upload = useUploadFixedMenuImage();
  const pick = async (camera: boolean) => {
    const result = camera
      ? await ImagePicker.launchCameraAsync({ mediaTypes: ['images'], quality: 1 })
      : await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images'], quality: 1 });
    const asset = result.canceled ? null : result.assets[0];
    if (asset) {
      upload.mutate(
        { kind, id, version, uri: asset.uri },
        {
          onSuccess: (root) => onUploaded(root.version),
          onError: (e) => {
            if (isApiError(e, 'VersionConflict')) onConflict();
          },
        },
      );
    }
  };
  return (
    <Card>
      <Text variant="subtitle">Foto</Text>
      {imageUrl ? <Image source={imageUrl} style={{ width: '100%', aspectRatio: 4 / 3, borderRadius: radius.md }} contentFit="cover" cachePolicy="disk" /> : <Text muted>Sem foto.</Text>}
      {canManage ? (
        <Row>
          <Button title="Câmera" icon="camera-outline" variant="secondary" loading={upload.isPending} onPress={() => void pick(true)} style={{ flex: 1 }} />
          <Button title="Galeria" icon="images-outline" variant="secondary" loading={upload.isPending} onPress={() => void pick(false)} style={{ flex: 1 }} />
        </Row>
      ) : null}
      {canManage && imageUrl ? <Button title="Remover foto" variant="ghost" onPress={onRemove} /> : null}
      {upload.error ? <Banner tone="danger" text={fixedMenuErrorText(upload.error)} /> : null}
    </Card>
  );
}
