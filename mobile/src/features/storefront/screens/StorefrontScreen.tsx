/**
 * 08 §8.10 `menu/storefront` — "Página de pedidos" (EP-002 F-210, RF-205, US-208/US-209; ADR-0044, ADR-0048).
 * Ler: `orders.view` (atendente compartilha o link e mostra o QR); alterar: `storefront.manage`. Tela de gestão:
 * `PUT /storefront` [M] com `If-Match`, `409` como mensagem e recarga (02 §2.3, exceção das telas de gestão).
 */
import { router, Stack } from 'expo-router';
import { useState } from 'react';
import { Switch, View } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import type { Storefront, UpdateStorefrontBody } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  ErrorState,
  MoneyInput,
  Row,
  Screen,
  Section,
  Skeleton,
  Text,
  TextField,
  space,
  useColors,
} from '@/components/ui';
import { PUBLIC_PAGE_BASE_URL } from '@/config';
import { confirmAction } from '@/lib/confirm';
import { maskBR } from '@/lib/phone';
import { shareLink } from '@/lib/share';
import { useIsOnline } from '@/stores/connectivity';
import { useCanWrite, usePermission } from '@/stores/session';
import { toast } from '@/stores/toast';

import { useStorefront, useUpdateStorefront } from '../hooks';
import { canPrintQr, printQr } from '../printQr';
import { QrCode } from '../QrCode';
import {
  formErrors,
  formFromStorefront,
  HOURS_TEXT_MAX,
  isFormDirty,
  maskZipCode,
  normalizeSlugInput,
  pageUrl,
  PUBLIC_NAME_MAX,
  SLUG_MAX,
  shareMessage,
  storeStatusInfo,
  toUpdateBody,
  type StorefrontForm,
} from '../storefront';

type UpdateMutation = ReturnType<typeof useUpdateStorefront>;

export const CONFLICT_TEXT = 'Outra pessoa alterou a página enquanto você editava. Nada foi gravado: os dados foram recarregados, confira e refaça.';

/** Mensagem de tela para o erro do `PUT` (409 com texto próprio; 422 com o motivo do servidor). */
export function storefrontErrorText(e: unknown): string {
  if (isApiError(e, 'VersionConflict')) return CONFLICT_TEXT;
  return errorMessage(e);
}

export function StorefrontScreen() {
  const storefront = useStorefront();
  // As mutações ficam aqui, fora do formulário que remonta a cada versão: o aviso do 409 sobrevive à recarga.
  const toggle = useUpdateStorefront();
  const save = useUpdateStorefront();
  if (storefront.isLoading) {
    return (
      <Screen>
        <Stack.Screen options={{ title: 'Página de pedidos' }} />
        <Skeleton lines={6} />
      </Screen>
    );
  }
  if (!storefront.data) {
    return (
      <Screen>
        <Stack.Screen options={{ title: 'Página de pedidos' }} />
        <ErrorState message={errorMessage(storefront.error)} onRetry={() => void storefront.refetch()} />
      </Screen>
    );
  }
  return <StorefrontView key={storefront.data.version} storefront={storefront.data} toggle={toggle} save={save} />;
}

function StorefrontView({ storefront: s, toggle, save }: { storefront: Storefront; toggle: UpdateMutation; save: UpdateMutation }) {
  const canEdit = useCanWrite('storefront.manage');
  const canManage = usePermission('storefront.manage');
  const c = useColors();
  const online = useIsOnline();
  const [form, setForm] = useState<StorefrontForm>(() => formFromStorefront(s));
  const [tried, setTried] = useState(false);
  const dirty = isFormDirty(s, form);
  const url = pageUrl(PUBLIC_PAGE_BASE_URL, s.slug);

  const set = <K extends keyof StorefrontForm>(key: K, value: StorefrontForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const runToggle = (overrides: Partial<Pick<UpdateStorefrontBody, 'isPageActive' | 'isAcceptingOrders'>>, done: string) => {
    save.reset();
    toggle.mutate({ body: toUpdateBody(s, null, overrides), version: s.version }, { onSuccess: () => toast(done, 'success') });
  };

  const onToggleActive = (active: boolean) =>
    confirmAction(
      active
        ? {
            title: 'Ativar a página de pedidos?',
            message: 'Quem abrir o link verá o cardápio do dia e poderá enviar pedidos para você aceitar no app.',
            confirmText: 'Ativar',
            onConfirm: () => runToggle({ isPageActive: true }, 'Página ativada'),
          }
        : {
            title: 'Desativar a página de pedidos?',
            message: 'Quem abrir o link verá "indisponível". Os pedidos que já chegaram continuam no app.',
            confirmText: 'Desativar',
            onConfirm: () => runToggle({ isPageActive: false }, 'Página desativada'),
          },
    );

  const onTogglePaused = (paused: boolean) =>
    confirmAction(
      paused
        ? {
            title: 'Pausar pedidos pelo site?',
            message: 'O cardápio continua aparecendo, mas ninguém consegue enviar pedido até você retomar.',
            confirmText: 'Pausar',
            onConfirm: () => runToggle({ isAcceptingOrders: false }, 'Pedidos pelo site pausados'),
          }
        : {
            title: 'Retomar pedidos pelo site?',
            message: 'A página volta a aceitar pedidos (com o expediente aberto).',
            confirmText: 'Retomar',
            onConfirm: () => runToggle({ isAcceptingOrders: true }, 'Pedidos pelo site retomados'),
          },
    );

  const localErrors = formErrors(form);
  const serverError = save.error;
  const fieldError = (field: string) => (tried ? localErrors[field] : undefined) ?? (isApiError(serverError) ? serverError.fieldError(field) : undefined);

  const submit = () => {
    setTried(true);
    if (Object.keys(localErrors).length > 0) return;
    const body = toUpdateBody(s, form);
    const send = () => {
      toggle.reset();
      save.mutate({ body, version: s.version }, { onSuccess: () => toast('Página salva', 'success') });
    };
    if (body.slug !== s.slug) {
      confirmAction({
        title: 'Trocar o endereço da página?',
        message: `O link novo será ${pageUrl(PUBLIC_PAGE_BASE_URL, body.slug) ?? body.slug}. O antigo (${s.slug}) continua levando para a página. Depois de 5 trocas, só pelo suporte.`,
        confirmText: 'Trocar endereço',
        onConfirm: send,
      });
    } else {
      send();
    }
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Página de pedidos' }} />
      <StatusCard storefront={s} />

      {canManage ? (
        <Card>
          <ToggleRow
            label="Página ativa"
            hint="Desativada, o link mostra “indisponível”."
            value={s.isPageActive}
            disabled={!canEdit || !online || dirty || toggle.isPending}
            onChange={onToggleActive}
          />
          <ToggleRow
            label="Pausar pedidos pelo site"
            hint="Para quando a cozinha lotar. O cardápio continua visível."
            value={!s.isAcceptingOrders}
            disabled={!canEdit || !online || dirty || toggle.isPending}
            onChange={onTogglePaused}
          />
          {dirty && canEdit ? <Text variant="small" muted>Salve ou desfaça as alterações abaixo antes de ativar ou pausar.</Text> : null}
          {toggle.error ? <Banner tone="danger" text={storefrontErrorText(toggle.error)} /> : null}
        </Card>
      ) : null}

      <LinkSection storefront={s} url={url} />

      {!canManage ? <Banner tone="info" text="Só o proprietário ou o gerente alteram a página. Você pode compartilhar o link." /> : null}

      <Section title="Endereço da página">
        <TextField
          label="Link (slug)"
          value={form.slug}
          onChangeText={(t) => set('slug', normalizeSlugInput(t))}
          editable={canEdit}
          autoCapitalize="none"
          autoCorrect={false}
          maxLength={SLUG_MAX}
          error={fieldError('slug')}
          hint={pageUrl(PUBLIC_PAGE_BASE_URL, form.slug || '…') ?? `…/${form.slug}`}
        />
        {s.previousSlugs.length > 0 ? (
          <Text variant="small" muted>
            Links antigos que ainda levam para cá: {s.previousSlugs.join(', ')}
          </Text>
        ) : null}
      </Section>

      <Section title="Perfil público">
        <TextField label="Nome na página" value={form.publicName} onChangeText={(t) => set('publicName', t)} editable={canEdit} maxLength={PUBLIC_NAME_MAX} error={fieldError('publicName')} />
        <TextField
          label="Telefone de contato (opcional)"
          value={form.contactPhone}
          onChangeText={(t) => set('contactPhone', maskBR(t))}
          editable={canEdit}
          keyboardType="phone-pad"
          placeholder="(34) 99999-1234"
          error={fieldError('contactPhone')}
        />
        <TextField
          label="Horário (opcional)"
          value={form.hoursText}
          onChangeText={(t) => set('hoursText', t)}
          editable={canEdit}
          maxLength={HOURS_TEXT_MAX}
          placeholder="Ex.: Seg a sáb, 10h30 às 14h"
          error={fieldError('hoursText')}
          hint="Só informativo: quem abre e fecha a página é o expediente."
        />
      </Section>

      <Section title="Endereço do restaurante">
        <Text variant="small" muted>
          Para ativar a página: rua, número, bairro, cidade e UF.
        </Text>
        <TextField label="Rua" value={form.street} onChangeText={(t) => set('street', t)} editable={canEdit} maxLength={120} error={fieldError('address.street')} />
        <Row style={{ alignItems: 'flex-start' }}>
          <View style={{ flex: 1 }}>
            <TextField label="Número" value={form.number} onChangeText={(t) => set('number', t)} editable={canEdit} maxLength={10} error={fieldError('address.number')} />
          </View>
          <View style={{ flex: 2 }}>
            <TextField label="Complemento (opcional)" value={form.complement} onChangeText={(t) => set('complement', t)} editable={canEdit} maxLength={60} error={fieldError('address.complement')} />
          </View>
        </Row>
        <TextField label="Bairro" value={form.neighborhood} onChangeText={(t) => set('neighborhood', t)} editable={canEdit} maxLength={60} error={fieldError('address.neighborhood')} />
        <Row style={{ alignItems: 'flex-start' }}>
          <View style={{ flex: 3 }}>
            <TextField label="Cidade" value={form.city} onChangeText={(t) => set('city', t)} editable={canEdit} maxLength={80} error={fieldError('address.city')} />
          </View>
          <View style={{ flex: 1 }}>
            <TextField
              label="UF"
              value={form.state}
              onChangeText={(t) => set('state', t.replace(/[^A-Za-z]/g, '').toUpperCase().slice(0, 2))}
              editable={canEdit}
              autoCapitalize="characters"
              error={fieldError('address.state')}
            />
          </View>
        </Row>
        <TextField label="CEP (opcional)" value={form.zipCode} onChangeText={(t) => set('zipCode', maskZipCode(t))} editable={canEdit} keyboardType="number-pad" error={fieldError('address.zipCode')} />
      </Section>

      <Section title="Retirada e entrega">
        <ToggleRow label="Retirada no balcão" value={form.offersPickup} disabled={!canEdit} onChange={(v) => set('offersPickup', v)} />
        {fieldError('offersPickup') ? <Text variant="small" style={{ color: c.danger }}>{fieldError('offersPickup')}</Text> : null}
        {!form.offersPickup ? <Banner tone="warning" text="Sem retirada, a página não recebe pedidos: a entrega ainda não está disponível." /> : null}
        <ToggleRow label="Entrega" hint="Disponível em breve: taxa por distância." value={form.offersDelivery} disabled onChange={() => {}} />
        <MoneyInput label="Preço por km" value={s.delivery.pricePerKm ?? '0.00'} onChange={() => {}} editable={false} />
        <MoneyInput label="Taxa mínima" value={s.delivery.minimumFee} onChange={() => {}} editable={false} />
        <TextField label="Raio máximo (km)" value={s.delivery.maxDistanceKm ? s.delivery.maxDistanceKm.replace('.', ',') : ''} placeholder="—" editable={false} />
        <Text variant="small" muted>
          Até lá, a página só aceita retirada.
        </Text>
      </Section>

      {serverError ? <Banner tone="danger" text={storefrontErrorText(serverError)} /> : null}
      {tried && Object.keys(localErrors).length > 0 ? <Banner tone="danger" text="Confira os campos destacados." /> : null}
      {canEdit ? (
        <>
          <Button title="Salvar" disabled={!dirty || !online} loading={save.isPending} onPress={submit} />
          {dirty ? (
            <Button
              title="Desfazer alterações"
              variant="ghost"
              onPress={() => {
                setForm(formFromStorefront(s));
                setTried(false);
                save.reset();
              }}
            />
          ) : null}
        </>
      ) : null}
    </Screen>
  );
}

function StatusCard({ storefront: s }: { storefront: Storefront }) {
  const info = storeStatusInfo(s);
  const canOpenDay = useCanWrite('cash.open');
  return (
    <Banner
      tone={info.tone}
      text={`Sua página está: ${info.title}. ${info.text}`}
      action={s.status === 'CLOSED' && canOpenDay ? { label: 'Abrir expediente', onPress: () => router.push('/business-day/open') } : undefined}
    />
  );
}

/** Link, compartilhar e QR (US-209). Sem base de link configurada no build, explica em vez de inventar um domínio. */
function LinkSection({ storefront: s, url }: { storefront: Storefront; url: string | null }) {
  const [showQr, setShowQr] = useState(false);
  if (!url) {
    return (
      <Section title="Link da página">
        <Banner tone="warning" text="O endereço da página ainda não está configurado nesta versão do app." />
      </Section>
    );
  }
  const share = async () => {
    const result = await shareLink({ title: s.publicName, message: shareMessage(s.publicName, url), url });
    if (result === 'copied') toast('Link copiado', 'success');
    if (result === 'failed') toast(`Não foi possível compartilhar. Copie o link: ${url}`, 'error');
  };
  return (
    <Section title="Link da página">
      <Card>
        <Text selectable variant="subtitle" testID="storefront-url">
          {url}
        </Text>
        {s.status === 'UNAVAILABLE' ? <Text variant="small" muted>Enquanto a página estiver desativada, quem abrir o link verá “indisponível”.</Text> : null}
        <Row style={{ flexWrap: 'wrap' }}>
          <Button title="Compartilhar link" icon="share-social-outline" onPress={() => void share()} style={{ flexGrow: 1 }} />
          <Button title={showQr ? 'Esconder QR code' : 'Mostrar QR code'} icon="qr-code-outline" variant="secondary" onPress={() => setShowQr(!showQr)} style={{ flexGrow: 1 }} />
        </Row>
        {showQr ? (
          <View style={{ gap: space.sm, alignItems: 'center' }}>
            <QrCode value={url} size={260} testID="storefront-qr" />
            <Text variant="small" muted style={{ textAlign: 'center' }}>
              {canPrintQr ? 'Imprima e deixe no balcão: o cliente aponta a câmera e cai na página.' : 'Mostre no balcão ou tire um print para imprimir.'}
            </Text>
            {canPrintQr ? (
              <Button
                title="Imprimir QR code"
                icon="print-outline"
                variant="secondary"
                onPress={() => {
                  if (!printQr({ publicName: s.publicName, url })) toast('O navegador bloqueou a janela de impressão.', 'error');
                }}
              />
            ) : null}
          </View>
        ) : null}
      </Card>
    </Section>
  );
}

function ToggleRow({ label, hint, value, disabled, onChange }: { label: string; hint?: string; value: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
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
