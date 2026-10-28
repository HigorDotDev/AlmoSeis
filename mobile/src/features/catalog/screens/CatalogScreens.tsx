/** 08 §8.10: `menu/products`, `menu/products/new`, `menu/products/[id]`, `menu/sizes`. */
import { Image } from 'expo-image';
import * as ImagePicker from 'expo-image-picker';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { Switch, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { MarmitaSize, Product, ProductKind } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  Chip,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  MoneyInput,
  Row,
  Screen,
  SegmentedControl,
  Skeleton,
  Text,
  TextField,
  radius,
  space,
} from '@/components/ui';
import { formatBRL, toCents } from '@/lib/money';
import { productKindNames, productKindSingular } from '@/lib/strings';
import { useCanWrite } from '@/stores/session';

import { useCreateProduct, useMarmitaSizes, useProduct, useProducts, useSaveSize, useUpdateProduct, useUploadProductImage } from '../hooks';

const KINDS: ProductKind[] = ['MAIN_DISH', 'SIDE_DISH', 'BEVERAGE', 'ADDON'];

export function ProductsScreen() {
  const [kind, setKind] = useState<ProductKind>('MAIN_DISH');
  const [search, setSearch] = useState('');
  const [inactive, setInactive] = useState(false);
  const products = useProducts({ kind, includeInactive: inactive });
  const canManage = useCanWrite('catalog.manage');
  const update = useUpdateProduct();
  const items = (products.data ?? []).filter((p) => p.name.toLowerCase().includes(search.trim().toLowerCase()));

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Pratos e bebidas' }} />
      <SegmentedControl value={kind} onChange={setKind} options={KINDS.map((k) => ({ value: k, label: productKindNames[k] }))} />
      <TextField label="Buscar" value={search} onChangeText={setSearch} />
      <Row style={{ justifyContent: 'space-between' }}>
        <Text>Mostrar inativos</Text>
        <Switch value={inactive} onValueChange={setInactive} accessibilityLabel="Mostrar inativos" />
      </Row>
      {canManage ? <Button title="Novo" icon="add" onPress={() => router.push({ pathname: '/menu/products/new', params: { kind } })} /> : null}
      {products.isLoading ? <Skeleton /> : null}
      {products.isError ? <ErrorState message={errorMessage(products.error)} onRetry={() => void products.refetch()} /> : null}
      {!products.isLoading && items.length === 0 ? <EmptyState title="Nada cadastrado" /> : null}
      {update.error ? <Banner tone="danger" text={errorMessage(update.error)} /> : null}
      {items.map((p) => (
        <Card key={p.id} onPress={() => router.push(`/menu/products/${p.id}`)} style={!p.isActive ? { opacity: 0.5 } : undefined}>
          <Row>
            {p.imageUrl ? <Image source={p.imageUrl} style={{ width: 48, height: 48, borderRadius: radius.sm }} cachePolicy="disk" /> : null}
            <View style={{ flex: 1 }}>
              <Text style={{ fontWeight: '700' }}>{p.name}</Text>
              <Text variant="small" muted>
                {p.defaultPrice ? formatBRL(p.defaultPrice) : 'Sem preço'}
                {p.category ? ` · ${p.category}` : ''}
                {!p.isActive ? ' · inativo' : ''}
              </Text>
            </View>
            {canManage && p.isActive ? (
              <Switch
                value={p.isAvailable}
                accessibilityLabel={`${p.name} disponível`}
                onValueChange={(isAvailable) => update.mutate({ id: p.id, body: { isAvailable }, version: p.version })}
              />
            ) : null}
          </Row>
        </Card>
      ))}
    </Screen>
  );
}

export function ProductFormScreen() {
  const { id, kind: kindParam, returnTo } = useLocalSearchParams<{ id?: string; kind?: ProductKind; returnTo?: string }>();
  const isNew = !id || id === 'new';
  const product = useProduct(isNew ? undefined : id);
  if (!isNew && product.isLoading) return <View style={{ padding: space.lg }}><Skeleton lines={4} /></View>;
  if (!isNew && !product.data) return <ErrorState message={errorMessage(product.error)} onRetry={() => void product.refetch()} />;
  return <ProductForm product={isNew ? null : product.data!} initialKind={kindParam ?? 'MAIN_DISH'} returnTo={returnTo} />;
}

/** Preço por tipo (ADR-0020): bebida e adicional exigem preço; prato pode ter (usado no PF); acompanhamento não precisa. */
function ProductForm({ product, initialKind, returnTo }: { product: Product | null; initialKind: ProductKind; returnTo?: string }) {
  const canManage = useCanWrite('catalog.manage');
  const create = useCreateProduct();
  const update = useUpdateProduct();
  const upload = useUploadProductImage();
  const [kind, setKind] = useState<ProductKind>(product?.kind ?? initialKind);
  const [name, setName] = useState(product?.name ?? '');
  const [description, setDescription] = useState(product?.description ?? '');
  const [category, setCategory] = useState(product?.category ?? '');
  const [price, setPrice] = useState<string>(product?.defaultPrice ?? '0.00');
  const [kitchen, setKitchen] = useState(product?.requiresPreparation ?? (initialKind === 'MAIN_DISH' || initialKind === 'SIDE_DISH'));
  const [deactivate, setDeactivate] = useState(false);
  const priceRequired = kind === 'BEVERAGE' || kind === 'ADDON';
  const error = create.error ?? update.error ?? upload.error;

  const save = () => {
    const defaultPrice = toCents(price) > 0 ? price : null;
    if (product) {
      update.mutate(
        { id: product.id, version: product.version, body: { name: name.trim(), description: description.trim() || null, category: category.trim() || null, defaultPrice, requiresPreparation: kitchen, ...(kind !== product.kind ? { kind } : {}) } },
        { onSuccess: () => router.back() },
      );
    } else {
      create.mutate(
        { kind, name: name.trim(), description: description.trim() || null, category: category.trim() || null, defaultPrice, requiresPreparation: kitchen },
        {
          // "+ Novo prato" do cardápio volta com o prato já incluído (RN-002.4/3.7).
          onSuccess: (p) => (returnTo ? router.replace({ pathname: returnTo as never, params: { addProductId: p.id } }) : router.replace(`/menu/products/${p.id}`)),
        },
      );
    }
  };

  const pickImage = async (camera: boolean) => {
    if (!product) return;
    const result = camera
      ? await ImagePicker.launchCameraAsync({ mediaTypes: ['images'], quality: 1 })
      : await ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images'], quality: 1 });
    const asset = result.canceled ? null : result.assets[0];
    if (asset) upload.mutate({ product, uri: asset.uri });
  };

  return (
    <Screen>
      <Stack.Screen options={{ title: product ? product.name : 'Novo produto' }} />
      <Text variant="small" muted>
        Tipo
      </Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {KINDS.map((k) => (
          <Chip key={k} label={productKindSingular[k]} selected={kind === k} onPress={() => setKind(k)} disabled={!canManage} />
        ))}
      </Row>
      {product ? <Text variant="small" muted>O tipo fica bloqueado depois que o produto é vendido ou entra num cardápio.</Text> : null}
      <TextField label="Nome" value={name} onChangeText={setName} editable={canManage} />
      <TextField label="Descrição (opcional)" value={description} onChangeText={setDescription} editable={canManage} />
      <TextField label="Categoria (opcional)" value={category} onChangeText={setCategory} editable={canManage} placeholder="Ex.: Carnes, Refrigerantes" />
      {kind !== 'SIDE_DISH' ? (
        <MoneyInput label={kind === 'MAIN_DISH' ? 'Preço padrão do PF (opcional)' : 'Preço'} value={price} onChange={setPrice} error={priceRequired && toCents(price) === 0 ? 'Informe o preço.' : null} />
      ) : null}
      <Row style={{ justifyContent: 'space-between' }}>
        <Text style={{ flex: 1 }}>Vai para a cozinha?</Text>
        <Switch value={kitchen} onValueChange={setKitchen} disabled={!canManage} accessibilityLabel="Vai para a cozinha" />
      </Row>

      {product ? (
        <Card>
          <Text variant="subtitle">Foto</Text>
          {product.imageUrl ? <Image source={product.imageUrl} style={{ width: '100%', aspectRatio: 4 / 3, borderRadius: radius.md }} contentFit="cover" cachePolicy="disk" /> : <Text muted>Sem foto.</Text>}
          {canManage ? (
            <Row>
              <Button title="Câmera" icon="camera-outline" variant="secondary" loading={upload.isPending} onPress={() => void pickImage(true)} style={{ flex: 1 }} />
              <Button title="Galeria" icon="images-outline" variant="secondary" loading={upload.isPending} onPress={() => void pickImage(false)} style={{ flex: 1 }} />
            </Row>
          ) : null}
          {canManage && product.imageUrl ? (
            <Button title="Remover foto" variant="ghost" onPress={() => update.mutate({ id: product.id, version: product.version, body: { imageStorageKey: null } })} />
          ) : null}
        </Card>
      ) : (
        <Text variant="small" muted>A foto pode ser incluída depois de salvar.</Text>
      )}

      {error ? <Banner tone="danger" text={errorMessage(error)} /> : null}
      {canManage ? <Button title="Salvar" onPress={save} disabled={name.trim().length < 2 || (priceRequired && toCents(price) === 0)} loading={create.isPending || update.isPending} /> : null}
      {canManage && product ? (
        product.isActive ? (
          <Button title="Excluir (desativar)" variant="danger" onPress={() => setDeactivate(true)} />
        ) : (
          <Button title="Reativar" variant="secondary" onPress={() => update.mutate({ id: product.id, version: product.version, body: { isActive: true } })} />
        )
      ) : null}
      <ConfirmDialog
        visible={deactivate}
        title="Desativar produto?"
        description="Ele some dos cardápios e da venda, mas continua no histórico (RN-002.6)."
        confirmLabel="Desativar"
        danger
        loading={update.isPending}
        onCancel={() => setDeactivate(false)}
        onConfirm={() => product && update.mutate({ id: product.id, version: product.version, body: { isActive: false } }, { onSuccess: () => { setDeactivate(false); router.back(); } })}
      />
    </Screen>
  );
}

export function SizesScreen() {
  const sizes = useMarmitaSizes(true);
  const canManage = useCanWrite('catalog.manage');
  const [editing, setEditing] = useState<Partial<MarmitaSize> | null>(null);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Tamanhos de marmita' }} />
      {sizes.isLoading ? <Skeleton /> : null}
      {sizes.data?.map((s) => (
        <Card key={s.id} onPress={canManage ? () => setEditing(s) : undefined} style={!s.isActive ? { opacity: 0.5 } : undefined}>
          <Row style={{ justifyContent: 'space-between' }}>
            <Text style={{ fontWeight: '700' }}>
              {s.name}
              {!s.isActive ? ' (inativo)' : ''}
            </Text>
            <Text>{formatBRL(s.price)}</Text>
          </Row>
        </Card>
      ))}
      {canManage ? <Button title="Novo tamanho" icon="add" onPress={() => setEditing({ name: '', price: '0.00', sortOrder: (sizes.data?.length ?? 0) + 1 })} /> : null}
      {editing ? <SizeForm key={editing.id ?? 'new'} size={editing} onDone={() => setEditing(null)} /> : null}
    </Screen>
  );
}

function SizeForm({ size, onDone }: { size: Partial<MarmitaSize>; onDone: () => void }) {
  const save = useSaveSize();
  const [name, setName] = useState(size.name ?? '');
  const [price, setPrice] = useState(size.price ?? '0.00');
  const [sortOrder, setSortOrder] = useState(String(size.sortOrder ?? 1));
  const [active, setActive] = useState(size.isActive ?? true);
  return (
    <Card>
      <TextField label="Nome (P, M, G…)" value={name} onChangeText={setName} />
      <MoneyInput label="Preço" value={price} onChange={setPrice} />
      <TextField label="Ordem" value={sortOrder} onChangeText={setSortOrder} keyboardType="number-pad" />
      {size.id ? (
        <Row style={{ justifyContent: 'space-between' }}>
          <Text>Ativo</Text>
          <Switch value={active} onValueChange={setActive} />
        </Row>
      ) : null}
      {save.error ? <Banner tone="danger" text={errorMessage(save.error)} /> : null}
      <Button
        title="Salvar tamanho"
        disabled={!name.trim() || toCents(price) === 0}
        loading={save.isPending}
        onPress={() => save.mutate({ id: size.id, version: size.version, name: name.trim(), price, sortOrder: Number(sortOrder) || 1, isActive: active }, { onSuccess: onDone })}
      />
      <Button title="Cancelar" variant="ghost" onPress={onDone} />
    </Card>
  );
}
