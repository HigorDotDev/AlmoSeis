/**
 * 08 §8.10 `menu/storefront` — "Página de pedidos" (EP-002 F-210, RF-205, US-208/US-209; ADR-0044, ADR-0048).
 * Restaurante (seção `MARMITA`) e, desde a fatia 5 do EP-003, lanchonete (seção `A_LA_CARTE`, cardápio fixo).
 * Ler: `orders.view` (atendente compartilha o link e mostra o QR); alterar: `storefront.manage`. Tela de gestão:
 * `PUT /storefront` [M] com `If-Match`, `409` como mensagem e recarga (02 §2.3, exceção das telas de gestão).
 */
import { router, Stack } from 'expo-router';
import { useState } from 'react';
import { Linking, Switch, View } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import type { DeliveryFeeMode, Storefront, UpdateStorefrontBody } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  ErrorState,
  MoneyInput,
  Row,
  Screen,
  Section,
  SegmentedControl,
  Skeleton,
  Text,
  TextField,
  space,
  useColors,
} from '@/components/ui';
import { PUBLIC_PAGE_BASE_URL } from '@/config';
import { useMarmitaSizes } from '@/features/catalog/hooks';
import { useFixedMenu } from '@/features/fixedMenu/hooks';
import { WebOrderPushPrompt } from '@/features/push/components';
import { confirmAction } from '@/lib/confirm';
import { formatBRL } from '@/lib/money';
import { NO_SITE_DISH_TEXT, NO_SITE_SIZE_TEXT } from '@/lib/sitePrice';
import { maskBR } from '@/lib/phone';
import { shareLink } from '@/lib/share';
import { useIsOnline } from '@/stores/connectivity';
import { useSettings } from '@/features/session/hooks';
import { useCanWrite, usePermission, useSession } from '@/stores/session';
import { storefrontSections, type StorefrontSection } from '@/features/tenant/establishment';
import { toast } from '@/stores/toast';

import { useStorefront, useUpdateStorefront } from '../hooks';
import { canPrintQr, printQr } from '../printQr';
import { QrCode } from '../QrCode';
import {
  aLaCarteSectionReady,
  formatCoordinate,
  formErrors,
  formFromStorefront,
  HOURS_TEXT_MAX,
  isFormDirty,
  mapsUrl,
  marmitaSectionReady,
  maskZipCode,
  normalizeSlugInput,
  pageDeliveryText,
  pageMenuWord,
  pageUrl,
  parseCoordinate,
  PUBLIC_NAME_MAX,
  SLUG_MAX,
  shareMessage,
  siteSaleWarnings,
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
  // Seleciona o `me` (referência estável) e deriva: um seletor que monta array novo re-renderiza sem parar.
  const me = useSession((st) => st.me);
  const sections = storefrontSections(me);
  const menuWord = pageMenuWord(sections);

  const set = <K extends keyof StorefrontForm>(key: K, value: StorefrontForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const runToggle = (overrides: Partial<Pick<UpdateStorefrontBody, 'isPageActive' | 'isAcceptingOrders' | 'isDeliveryEnabled' | 'location'>>, done: string) => {
    save.reset();
    toggle.mutate({ body: toUpdateBody(s, null, overrides), version: s.version }, { onSuccess: () => toast(done, 'success') });
  };

  const onToggleActive = (active: boolean) =>
    confirmAction(
      active
        ? {
            title: 'Ativar a página de pedidos?',
            message: `Quem abrir o link verá ${menuWord} e poderá enviar pedidos para você aceitar no app.`,
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

  const onToggleDeliveryPaused = (paused: boolean) =>
    confirmAction(
      paused
        ? {
            title: 'Pausar entrega pela página?',
            message: 'A página continua aceitando retirada. Ninguém consegue pedir entrega até você retomar.',
            confirmText: 'Pausar entrega',
            onConfirm: () => runToggle({ isDeliveryEnabled: false }, 'Entrega pela página pausada'),
          }
        : {
            title: 'Retomar entrega pela página?',
            message: 'A página volta a oferecer entrega, se o resto da configuração estiver completo.',
            confirmText: 'Retomar entrega',
            onConfirm: () => runToggle({ isDeliveryEnabled: true }, 'Entrega pela página retomada'),
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
      {/* ADR-0049 item 6: a permissão de notificação é pedida no primeiro uso da página de pedidos. */}
      <WebOrderPushPrompt />

      {canManage ? (
        <Card>
          <ToggleRow
            label="Página ativa"
            hint={s.isPageActive ? `Ativa: quem abre o link vê ${menuWord} e pode pedir.` : 'Desativada: o link mostra “indisponível”.'}
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
          {s.offersDelivery ? (
            <ToggleRow
              label="Pausar entrega"
              hint={s.isDeliveryEnabled ? 'Para quando faltar entregador: a página fica só com retirada.' : 'Pausada: a página oferece só retirada.'}
              value={!s.isDeliveryEnabled}
              disabled={!canEdit || !online || dirty || toggle.isPending}
              onChange={onToggleDeliveryPaused}
            />
          ) : null}
          {dirty && canEdit ? <Text variant="small" muted>Salve ou desfaça as alterações abaixo antes de ativar ou pausar.</Text> : null}
          <SiteSaleWarnings sections={sections} />
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
        {!form.offersPickup && !s.pageDelivery.available ? <Banner tone="warning" text="Sem retirada e sem entrega disponível, a página não recebe pedidos." /> : null}
        <ToggleRow label="Entrega" hint="O cliente informa o endereço e a página calcula a taxa." value={form.offersDelivery} disabled={!canEdit} onChange={(v) => set('offersDelivery', v)} />
        {form.offersDelivery ? <DeliveryFields form={form} set={set} canEdit={canEdit} fieldError={fieldError} /> : null}
        <PageDeliveryStatus storefront={s} />
      </Section>

      {form.offersDelivery && form.feeMode === 'DISTANCE' ? (
        <LocationSection storefront={s} canEdit={canEdit && online && !dirty && !toggle.isPending} dirty={dirty} onSave={(location, done) => runToggle({ location }, done)} />
      ) : null}

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

const SECTION_WARNING: Record<StorefrontSection, { text: string; partial: string; action: string; href: string }> = {
  MARMITA: {
    text: NO_SITE_SIZE_TEXT,
    partial: 'As marmitas não aparecem na página: nenhum tamanho de marmita ativo vende no site.',
    action: 'Abrir tamanhos de marmita',
    href: '/menu/sizes',
  },
  A_LA_CARTE: {
    text: NO_SITE_DISH_TEXT,
    partial: 'O cardápio fixo não aparece na página: nenhum prato ou combo ativo vende no site.',
    action: 'Abrir o Cardápio',
    href: '/menu/fixed-menu',
  },
};

/**
 * Antes do `422` em `isPageActive` (06 §6.18): ativar exige algo que vende no site numa das seções. `MARMITA`: um
 * tamanho de marmita ativo (`['marmitaSizes']`, ADR-0057 item 3); `A_LA_CARTE`: um prato (ou tamanho) ou combo ativo
 * (`['fixedMenu']`, RN-316.5). Cada lista só é lida se a seção existe. Com os dois comportamentos, a seção vazia que
 * não impede a ativação vira um aviso informativo. O servidor decide: o `422` dele aparece logo abaixo.
 */
function SiteSaleWarnings({ sections }: { sections: StorefrontSection[] }) {
  const canEditCatalog = useCanWrite('catalog.manage');
  const withMarmita = sections.includes('MARMITA');
  const withALaCarte = sections.includes('A_LA_CARTE');
  const sizes = useMarmitaSizes(false, withMarmita);
  const fixedMenu = useFixedMenu(true, withALaCarte);
  const warnings = siteSaleWarnings(sections, {
    MARMITA: withMarmita && sizes.data ? marmitaSectionReady(sizes.data) : undefined,
    A_LA_CARTE: withALaCarte && fixedMenu.data ? aLaCarteSectionReady(fixedMenu.data) : undefined,
  });
  return (
    <>
      {warnings.map(({ section, blocking }) => {
        const w = SECTION_WARNING[section];
        return (
          <Banner
            key={section}
            tone={blocking ? 'warning' : 'info'}
            text={blocking ? w.text : w.partial}
            action={canEditCatalog ? { label: w.action, onPress: () => router.push(w.href as never) } : undefined}
          />
        );
      })}
    </>
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
        {s.status === 'UNAVAILABLE' ? (
          <Text variant="small" muted>
            {s.isPageActive ? 'Enquanto a página estiver indisponível, quem abrir o link verá “indisponível”.' : 'Enquanto a página estiver desativada, quem abrir o link verá “indisponível”.'}
          </Text>
        ) : null}
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

type SetField = <K extends keyof StorefrontForm>(key: K, value: StorefrontForm[K]) => void;

const FEE_MODES: { value: DeliveryFeeMode; label: string }[] = [
  { value: 'DISTANCE', label: 'Por distância' },
  { value: 'FIXED', label: 'Taxa fixa' },
];

/** Regra da taxa (ADR-0048): por distância (km × preço, com mínimo e raio opcional) ou a taxa fixa do estabelecimento. */
function DeliveryFields({ form, set, canEdit, fieldError }: { form: StorefrontForm; set: SetField; canEdit: boolean; fieldError: (f: string) => string | undefined }) {
  const settings = useSettings();
  return (
    <View style={{ gap: space.md }}>
      {canEdit ? (
        <SegmentedControl value={form.feeMode} onChange={(v) => set('feeMode', v)} options={FEE_MODES} />
      ) : (
        <Text>Taxa: {FEE_MODES.find((m) => m.value === form.feeMode)?.label}</Text>
      )}
      {form.feeMode === 'DISTANCE' ? (
        <>
          <MoneyInput label="Preço por km" value={form.pricePerKm} onChange={(v) => set('pricePerKm', v)} editable={canEdit} error={fieldError('delivery.pricePerKm')} testID="price-per-km" />
          <MoneyInput label="Taxa mínima" value={form.minimumFee} onChange={(v) => set('minimumFee', v)} editable={canEdit} error={fieldError('delivery.minimumFee')} testID="minimum-fee" />
          <TextField
            label="Raio máximo em km (opcional)"
            value={form.maxDistanceKm}
            onChangeText={(t) => set('maxDistanceKm', t.replace(/[^0-9,.]/g, '').slice(0, 5))}
            editable={canEdit}
            keyboardType="decimal-pad"
            placeholder="Sem limite"
            error={fieldError('delivery.maxDistanceKm')}
            hint="A taxa é a distância da rota × o preço por km, nunca abaixo da mínima."
          />
        </>
      ) : (
        <Text variant="small" muted>
          {settings.data
            ? `Toda entrega pela página cobra ${formatBRL(settings.data.defaultDeliveryFee)}, a taxa padrão de "Configuração de preços". Sem mapa nem cálculo de distância.`
            : 'Toda entrega pela página cobra a taxa padrão de "Configuração de preços".'}
        </Text>
      )}
    </View>
  );
}

/** `pageDelivery` (06 §6.18): se a página oferece entrega agora e, se não, o que falta. Reflete o que está salvo. */
function PageDeliveryStatus({ storefront: s }: { storefront: Storefront }) {
  if (s.pageDelivery.available) return <Banner tone="success" text="A página oferece entrega agora." />;
  if (!s.offersDelivery) return null;
  const { steps, provider } = pageDeliveryText(s.pageDelivery);
  return (
    <View style={{ gap: space.sm }} testID="page-delivery-missing">
      {steps ? <Banner tone="info" text={steps} /> : null}
      {provider ? <Banner tone="warning" text={provider} /> : null}
    </View>
  );
}

/**
 * Ponto do restaurante (ADR-0048 item 3), sem módulo nativo de mapa: as coordenadas, "Ver no mapa" (Google Maps no
 * navegador ou no app de mapas) e "Confirmar este ponto". Correção à mão em modo avançado. Mudar o endereço apaga o
 * ponto e a API geocodifica de novo; o formulário nunca manda o ponto (sem `location`, o servidor o mantém).
 */
function LocationSection({
  storefront: s,
  canEdit,
  dirty,
  onSave,
}: {
  storefront: Storefront;
  canEdit: boolean;
  dirty: boolean;
  onSave: (location: { latitude: number; longitude: number; isConfirmed: boolean }, done: string) => void;
}) {
  const manage = usePermission('storefront.manage');
  const l = s.location;
  const [advanced, setAdvanced] = useState(false);
  const [lat, setLat] = useState(l ? formatCoordinate(l.latitude) : '');
  const [lng, setLng] = useState(l ? formatCoordinate(l.longitude) : '');
  const latitude = parseCoordinate(lat, 90);
  const longitude = parseCoordinate(lng, 180);

  const confirm = () => {
    if (!l) return;
    confirmAction({
      title: 'Confirmar o ponto do restaurante?',
      message: `As distâncias das entregas serão medidas a partir de ${formatCoordinate(l.latitude)}, ${formatCoordinate(l.longitude)}. Confira no mapa antes.`,
      confirmText: 'Confirmar ponto',
      onConfirm: () => onSave({ latitude: l.latitude, longitude: l.longitude, isConfirmed: true }, 'Ponto confirmado'),
    });
  };

  const saveManual = () => {
    if (latitude === null || longitude === null) return;
    confirmAction({
      title: 'Usar este ponto?',
      message: `As distâncias das entregas serão medidas a partir de ${formatCoordinate(latitude)}, ${formatCoordinate(longitude)}.`,
      confirmText: 'Usar e confirmar',
      onConfirm: () => onSave({ latitude, longitude, isConfirmed: true }, 'Ponto confirmado'),
    });
  };

  return (
    <Section title="Ponto do restaurante">
      <Card>
        {l ? (
          <>
            <Text selectable testID="storefront-location" style={{ fontWeight: '700', fontVariant: ['tabular-nums'] }}>
              {`${formatCoordinate(l.latitude)}, ${formatCoordinate(l.longitude)}`}
            </Text>
            <Banner
              tone={l.isConfirmed ? 'success' : 'warning'}
              text={l.isConfirmed ? 'Ponto confirmado: as distâncias saem daqui.' : 'Ponto encontrado pelo endereço, ainda não confirmado. Veja no mapa e confirme.'}
            />
          </>
        ) : (
          <Banner tone="info" text="Sem ponto: salve o endereço completo para localizarmos o restaurante, ou informe as coordenadas no modo avançado." />
        )}
        <Row style={{ flexWrap: 'wrap' }}>
          {l ? <Button title="Ver no mapa" icon="map-outline" variant="secondary" onPress={() => void Linking.openURL(mapsUrl(l))} style={{ flexGrow: 1 }} /> : null}
          {manage && l && !l.isConfirmed ? <Button title="Confirmar este ponto" icon="checkmark" disabled={!canEdit} onPress={confirm} style={{ flexGrow: 1 }} /> : null}
        </Row>
        {manage && dirty ? (
          <Text variant="small" muted>
            Salve ou desfaça as alterações antes de mexer no ponto.
          </Text>
        ) : null}
        {manage ? <Button title={advanced ? 'Esconder correção manual' : 'Corrigir o ponto à mão (avançado)'} variant="ghost" onPress={() => setAdvanced(!advanced)} /> : null}
        {manage && advanced ? (
          <View style={{ gap: space.sm }}>
            <Text variant="small" muted>
              No Google Maps, toque e segure no lugar exato do restaurante e copie os números (latitude, longitude).
            </Text>
            <Row style={{ alignItems: 'flex-start' }}>
              <View style={{ flex: 1 }}>
                <TextField label="Latitude" value={lat} onChangeText={setLat} keyboardType="numbers-and-punctuation" error={lat.trim() && latitude === null ? 'Entre -90 e 90.' : null} />
              </View>
              <View style={{ flex: 1 }}>
                <TextField label="Longitude" value={lng} onChangeText={setLng} keyboardType="numbers-and-punctuation" error={lng.trim() && longitude === null ? 'Entre -180 e 180.' : null} />
              </View>
            </Row>
            {latitude !== null && longitude !== null ? (
              <Button title="Ver este ponto no mapa" icon="map-outline" variant="secondary" onPress={() => void Linking.openURL(mapsUrl({ latitude, longitude }))} />
            ) : null}
            <Button title="Usar este ponto" disabled={!canEdit || latitude === null || longitude === null} onPress={saveManual} />
          </View>
        ) : null}
      </Card>
    </Section>
  );
}
