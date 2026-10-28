import { router } from 'expo-router';
import { RefreshControl, ScrollView, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import { Button, Card, ErrorState, Row, Skeleton, Text, space, useColors } from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { formatBusinessDate } from '@/lib/dates';
import { formatBRL } from '@/lib/money';
import { useCanWrite, usePermission } from '@/stores/session';

import { ClosingSummaryView } from '../components';
import { useClosingHistory, useClosingSummary } from '../hooks';

/** 08 §8.9 Aba Fechamento. Atualizada por `ClosingUpdated` ou puxando para baixo. */
export default function ClosingTab() {
  const c = useColors();
  const day = useCurrentBusinessDay();
  const summary = useClosingSummary(undefined, !!day.data);
  const canMove = useCanWrite('cash.movement');
  // Com SUSPENDED o fechamento continua liberado no servidor (06 §6.1, 402); o app só não mostra a ação a quem não tem permissão.
  const canFinalize = usePermission('closing.finalize');
  const canOpen = useCanWrite('cash.open');

  if (day.isLoading) return <View style={{ padding: space.lg }}><Skeleton /></View>;

  if (!day.data) {
    return (
      <ScrollView style={{ backgroundColor: c.bg }} contentContainerStyle={{ padding: space.lg, gap: space.md }}>
        <Card>
          <Text variant="subtitle">Nenhum expediente aberto</Text>
          {canOpen ? <Button title="Abrir expediente" icon="lock-open-outline" onPress={() => router.push('/business-day/open')} /> : null}
        </Card>
        <RecentClosings />
      </ScrollView>
    );
  }

  return (
    <ScrollView
      style={{ backgroundColor: c.bg }}
      contentContainerStyle={{ padding: space.lg, gap: space.md, paddingBottom: 48 }}
      refreshControl={<RefreshControl refreshing={summary.isRefetching} onRefresh={() => void summary.refetch()} />}
    >
      <Text variant="title">Parcial de {formatBusinessDate(day.data.businessDate)}</Text>
      {summary.isLoading ? <Skeleton lines={4} /> : null}
      {summary.isError ? <ErrorState message={errorMessage(summary.error)} onRetry={() => void summary.refetch()} /> : null}
      {summary.data ? <ClosingSummaryView summary={summary.data} /> : null}
      <Row>
        {canMove ? <Button title="Suprimento/Retirada" variant="secondary" onPress={() => router.push('/closing/cash-movement')} style={{ flex: 1 }} /> : null}
        <Button title="Histórico" variant="secondary" onPress={() => router.push('/closing/history')} style={{ flex: 1 }} />
      </Row>
      {canFinalize ? <Button title="Fechar expediente" icon="lock-closed-outline" onPress={() => router.push('/closing/finalize')} /> : null}
    </ScrollView>
  );
}

function RecentClosings() {
  const history = useClosingHistory({});
  const items = history.data?.pages.flatMap((p) => p.items).slice(0, 7) ?? [];
  if (!items.length) return null;
  return (
    <View style={{ gap: space.sm }}>
      <Text variant="subtitle">Últimos fechamentos</Text>
      {items.map((h) => (
        <Card key={h.dailyClosingId} onPress={() => router.push(`/closing/${h.dailyClosingId}`)}>
          <Row style={{ justifyContent: 'space-between' }}>
            <Text style={{ fontWeight: '700' }}>{formatBusinessDate(h.businessDate, true)}</Text>
            <Text>{formatBRL(h.totalSold)}</Text>
          </Row>
        </Card>
      ))}
    </View>
  );
}
