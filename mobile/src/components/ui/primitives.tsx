/** Blocos básicos: texto, tela, card, botão, campo, chip, segmentado, estados de tela. */
import { Ionicons } from '@expo/vector-icons';
import type { ComponentProps, ReactNode } from 'react';
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  StyleSheet,
  Text as RNText,
  TextInput,
  View,
  type StyleProp,
  type TextInputProps,
  type TextProps,
  type TextStyle,
  type ViewStyle,
} from 'react-native';

import { font, radius, space, TOUCH, useColors } from './theme';

export type IconName = ComponentProps<typeof Ionicons>['name'];

export function Text({
  variant = 'body',
  muted,
  style,
  ...props
}: TextProps & { variant?: 'title' | 'subtitle' | 'body' | 'small' | 'big'; muted?: boolean }) {
  const c = useColors();
  const size = { title: font.xl, subtitle: font.lg, body: font.md, small: font.sm, big: font.xxl }[variant];
  const weight: TextStyle['fontWeight'] = variant === 'title' || variant === 'big' || variant === 'subtitle' ? '700' : '400';
  return <RNText {...props} style={[{ color: muted ? c.textMuted : c.text, fontSize: size, fontWeight: weight }, style]} />;
}

export function Screen({ children, scroll = true, style }: { children: ReactNode; scroll?: boolean; style?: StyleProp<ViewStyle> }) {
  const c = useColors();
  if (!scroll) {
    return <View style={[{ flex: 1, backgroundColor: c.bg }, style]}>{children}</View>;
  }
  return (
    <ScrollView style={{ flex: 1, backgroundColor: c.bg }} contentContainerStyle={[{ padding: space.lg, gap: space.md, paddingBottom: 96 }, style]} keyboardShouldPersistTaps="handled">
      {children}
    </ScrollView>
  );
}

export function Card({ children, style, onPress, accessibilityLabel }: { children: ReactNode; style?: StyleProp<ViewStyle>; onPress?: () => void; accessibilityLabel?: string }) {
  const c = useColors();
  const base: ViewStyle = { backgroundColor: c.surface, borderRadius: radius.md, borderWidth: StyleSheet.hairlineWidth, borderColor: c.border, padding: space.md, gap: space.sm };
  if (onPress) {
    return (
      <Pressable onPress={onPress} accessibilityRole="button" accessibilityLabel={accessibilityLabel} style={({ pressed }) => [base, pressed && { opacity: 0.7 }, style]}>
        {children}
      </Pressable>
    );
  }
  return <View style={[base, style]}>{children}</View>;
}

export function Row({ children, style, gap = space.sm }: { children: ReactNode; style?: StyleProp<ViewStyle>; gap?: number }) {
  return <View style={[{ flexDirection: 'row', alignItems: 'center', gap }, style]}>{children}</View>;
}

export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost';

export function Button({
  title,
  onPress,
  variant = 'primary',
  disabled,
  loading,
  icon,
  style,
  accessibilityHint,
  testID,
}: {
  title: string;
  onPress: () => void;
  variant?: ButtonVariant;
  disabled?: boolean;
  /** Bloqueia toque duplo enquanto envia (08 §8.11). */
  loading?: boolean;
  icon?: IconName;
  style?: StyleProp<ViewStyle>;
  accessibilityHint?: string;
  testID?: string;
}) {
  const c = useColors();
  const inactive = disabled || loading;
  const bg = { primary: c.primary, secondary: c.surfaceAlt, danger: c.danger, ghost: 'transparent' }[variant];
  const fg = { primary: c.primaryText, secondary: c.text, danger: '#FFFFFF', ghost: c.primary }[variant];
  return (
    <Pressable
      testID={testID}
      disabled={!!inactive}
      onPress={inactive ? undefined : onPress}
      accessibilityRole="button"
      accessibilityLabel={title}
      accessibilityHint={accessibilityHint}
      accessibilityState={{ disabled: !!inactive, busy: !!loading }}
      style={({ pressed }) => [
        { minHeight: TOUCH, borderRadius: radius.md, backgroundColor: bg, paddingHorizontal: space.lg, alignItems: 'center', justifyContent: 'center', flexDirection: 'row', gap: space.sm },
        variant === 'secondary' && { borderWidth: 1, borderColor: c.border },
        inactive && { opacity: 0.45 },
        pressed && !inactive && { opacity: 0.8 },
        style,
      ]}
    >
      {loading ? <ActivityIndicator color={fg} /> : icon ? <Ionicons name={icon} size={20} color={fg} /> : null}
      <RNText style={{ color: fg, fontSize: font.md, fontWeight: '700' }}>{title}</RNText>
    </Pressable>
  );
}

export function IconButton({ icon, label, onPress, color, disabled }: { icon: IconName; label: string; onPress: () => void; color?: string; disabled?: boolean }) {
  const c = useColors();
  return (
    <Pressable
      disabled={!!disabled}
      onPress={disabled ? undefined : onPress}
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled: !!disabled }}
      hitSlop={8}
      style={({ pressed }) => [{ width: TOUCH, height: TOUCH, alignItems: 'center', justifyContent: 'center', opacity: disabled ? 0.4 : pressed ? 0.6 : 1 }]}
    >
      <Ionicons name={icon} size={24} color={color ?? c.text} />
    </Pressable>
  );
}

export function TextField({
  label,
  error,
  hint,
  style,
  ...props
}: TextInputProps & { label: string; error?: string | null; hint?: string }) {
  const c = useColors();
  return (
    <View style={{ gap: space.xs }}>
      <Text variant="small" muted>
        {label}
      </Text>
      <TextInput
        accessibilityLabel={label}
        placeholderTextColor={c.textMuted}
        {...props}
        style={[
          { minHeight: TOUCH, borderWidth: 1, borderColor: error ? c.danger : c.border, borderRadius: radius.md, paddingHorizontal: space.md, color: c.text, backgroundColor: c.surface, fontSize: font.md },
          props.multiline && { minHeight: 80, textAlignVertical: 'top', paddingTop: space.sm },
          style,
        ]}
      />
      {error ? (
        <Text variant="small" style={{ color: c.danger }}>
          {error}
        </Text>
      ) : hint ? (
        <Text variant="small" muted>
          {hint}
        </Text>
      ) : null}
    </View>
  );
}

export function Chip({ label, selected, onPress, disabled, strike }: { label: string; selected?: boolean; onPress?: () => void; disabled?: boolean; strike?: boolean }) {
  const c = useColors();
  return (
    <Pressable
      disabled={!!disabled}
      onPress={disabled ? undefined : onPress}
      accessibilityRole="checkbox"
      accessibilityState={{ checked: !!selected, disabled: !!disabled }}
      accessibilityLabel={label}
      style={{
        minHeight: 40,
        paddingHorizontal: space.md,
        borderRadius: radius.pill,
        borderWidth: 1,
        borderColor: selected ? c.primary : c.border,
        backgroundColor: selected ? c.primary : c.surface,
        justifyContent: 'center',
        opacity: disabled ? 0.45 : 1,
      }}
    >
      <RNText style={{ color: selected ? c.primaryText : c.text, fontSize: font.md, textDecorationLine: strike ? 'line-through' : 'none' }}>{label}</RNText>
    </Pressable>
  );
}

export function SegmentedControl<T extends string>({ options, value, onChange }: { options: { value: T; label: string }[]; value: T; onChange: (v: T) => void }) {
  const c = useColors();
  return (
    <View accessibilityRole="tablist" style={{ flexDirection: 'row', backgroundColor: c.surfaceAlt, borderRadius: radius.md, padding: 3 }}>
      {options.map((o) => {
        const active = o.value === value;
        return (
          <Pressable
            key={o.value}
            onPress={() => onChange(o.value)}
            accessibilityRole="tab"
            accessibilityState={{ selected: active }}
            style={{ flex: 1, minHeight: 42, alignItems: 'center', justifyContent: 'center', borderRadius: radius.sm, backgroundColor: active ? c.surface : 'transparent' }}
          >
            <RNText style={{ color: active ? c.text : c.textMuted, fontWeight: active ? '700' : '400', fontSize: font.sm }}>{o.label}</RNText>
          </Pressable>
        );
      })}
    </View>
  );
}

export function Section({ title, children, right }: { title: string; children: ReactNode; right?: ReactNode }) {
  return (
    <View style={{ gap: space.sm }}>
      <Row style={{ justifyContent: 'space-between' }}>
        <Text variant="subtitle">{title}</Text>
        {right}
      </Row>
      {children}
    </View>
  );
}

export function Divider() {
  const c = useColors();
  return <View style={{ height: StyleSheet.hairlineWidth, backgroundColor: c.border }} />;
}

export function KeyValue({ label, value, strong, color }: { label: string; value: ReactNode; strong?: boolean; color?: string }) {
  return (
    <Row style={{ justifyContent: 'space-between' }}>
      <Text muted={!strong} style={strong ? { fontWeight: '700' } : undefined}>
        {label}
      </Text>
      {typeof value === 'string' ? <Text style={[strong && { fontWeight: '700' }, color ? { color } : null]}>{value}</Text> : value}
    </Row>
  );
}

export function Banner({ tone, text, action }: { tone: 'info' | 'warning' | 'danger' | 'success'; text: string; action?: { label: string; onPress: () => void } }) {
  const c = useColors();
  const fg = { info: c.info, warning: c.warning, danger: c.danger, success: c.success }[tone];
  const bg = { info: c.infoBg, warning: c.warningBg, danger: c.dangerBg, success: c.successBg }[tone];
  return (
    <View accessibilityRole="alert" style={{ backgroundColor: bg, padding: space.md, borderRadius: radius.md, gap: space.sm }}>
      <RNText style={{ color: fg, fontSize: font.sm, fontWeight: '600' }}>{text}</RNText>
      {action ? (
        <Pressable onPress={action.onPress} accessibilityRole="button" style={{ minHeight: 36, justifyContent: 'center' }}>
          <RNText style={{ color: fg, fontWeight: '700', textDecorationLine: 'underline' }}>{action.label}</RNText>
        </Pressable>
      ) : null}
    </View>
  );
}

export function EmptyState({ icon = 'file-tray-outline', title, text, action }: { icon?: IconName; title: string; text?: string; action?: ReactNode }) {
  const c = useColors();
  return (
    <View style={{ alignItems: 'center', padding: space.xl, gap: space.sm }}>
      <Ionicons name={icon} size={40} color={c.textMuted} />
      <Text variant="subtitle" style={{ textAlign: 'center' }}>
        {title}
      </Text>
      {text ? (
        <Text muted style={{ textAlign: 'center' }}>
          {text}
        </Text>
      ) : null}
      {action}
    </View>
  );
}

export function ErrorState({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <EmptyState
      icon="cloud-offline-outline"
      title="Não foi possível carregar"
      text={message}
      action={onRetry ? <Button title="Tentar de novo" variant="secondary" onPress={onRetry} /> : undefined}
    />
  );
}

export function Skeleton({ lines = 3 }: { lines?: number }) {
  const c = useColors();
  return (
    <View accessibilityLabel="Carregando" style={{ gap: space.sm }}>
      {Array.from({ length: lines }, (_, i) => (
        <View key={i} style={{ height: 64, borderRadius: radius.md, backgroundColor: c.surfaceAlt }} />
      ))}
    </View>
  );
}

export function Fab({ label, icon, onPress, disabled, offset = 0 }: { label: string; icon: IconName; onPress: () => void; disabled?: boolean; offset?: number }) {
  const c = useColors();
  return (
    <Pressable
      disabled={!!disabled}
      onPress={disabled ? undefined : onPress}
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled: !!disabled }}
      style={({ pressed }) => ({
        position: 'absolute',
        right: space.lg,
        bottom: space.lg + offset,
        minHeight: 56,
        paddingHorizontal: space.lg,
        borderRadius: radius.pill,
        backgroundColor: c.primary,
        flexDirection: 'row',
        alignItems: 'center',
        gap: space.sm,
        opacity: disabled ? 0.45 : pressed ? 0.85 : 1,
        elevation: 4,
        shadowColor: '#000',
        shadowOpacity: 0.2,
        shadowRadius: 6,
        shadowOffset: { width: 0, height: 3 },
      })}
    >
      <Ionicons name={icon} size={22} color={c.primaryText} />
      <RNText style={{ color: c.primaryText, fontWeight: '700', fontSize: font.md }}>{label}</RNText>
    </Pressable>
  );
}
