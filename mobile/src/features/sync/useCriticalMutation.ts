/**
 * Hook das escritas [I]/[M] (08 §8.5). A tela confirma (ConfirmDialog) e chama `run(vars)`; o hook cuida da
 * chave, da pendência, do resultado desconhecido e do conflito de versão.
 */
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';

import type { CriticalRequest } from '@/api/critical';
import { errorMessage, type ApiError } from '@/api/errors';
import { useConnectivity } from '@/stores/connectivity';
import { useSession } from '@/stores/session';
import { toast } from '@/stores/toast';

import { invalidateForPath, resolveUnknown, runCritical, type CriticalOutcome } from './operations';
import { pendingOpsRepo } from '@/db/pendingOpsRepo';

export type CriticalState = 'idle' | 'sending' | 'unknown' | 'failed';

export interface CriticalMutationOptions<TVars, TResult> {
  request: (vars: TVars) => CriticalRequest;
  describe: (vars: TVars) => string;
  /** Resposta 2xx: atualize o cache com o recurso devolvido (a próxima escrita já usa a versão nova). */
  onConfirmed?: (result: TResult, vars: TVars, version: number | null) => void;
  /** Resultado desconhecido que se revelou concluído: a resposta se perdeu, então só há o id do recurso. */
  onResolvedCompleted?: (resourceId: string | null) => void;
  onRejected?: (error: ApiError, vars: TVars) => void;
  onConflict?: (current: unknown, error: ApiError, vars: TVars) => void;
  successMessage?: (result: TResult, vars: TVars) => string | null;
  draftId?: string | null;
  idempotent?: boolean;
}

export function useCriticalMutation<TVars, TResult>(options: CriticalMutationOptions<TVars, TResult>) {
  const queryClient = useQueryClient();
  const tenantId = useSession((s) => s.me?.tenant.id);
  const employeeId = useSession((s) => s.me?.employee.id);
  const online = useConnectivity((s) => s.online);
  const [state, setState] = useState<CriticalState>('idle');
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [pendingKey, setPendingKey] = useState<string | null>(null);
  const unknownKey = useRef<string | null>(null);
  const optionsRef = useRef(options);
  useEffect(() => {
    optionsRef.current = options;
  });

  const resolve = useCallback(async () => {
    const key = unknownKey.current;
    if (!key) return;
    const op = await pendingOpsRepo.get(key);
    if (!op) {
      setState('idle');
      return;
    }
    const result = await resolveUnknown(op);
    if (result.kind === 'completed') {
      unknownKey.current = null;
      setPendingKey(null);
      invalidateForPath(queryClient, op.path);
      setState('idle');
      if (result.responseStatus !== null && result.responseStatus < 300) {
        toast('Operação confirmada pelo servidor.');
        optionsRef.current.onResolvedCompleted?.(result.resourceId);
      } else {
        setError('O servidor recusou a operação. Confira os dados e tente de novo.');
      }
    } else if (result.kind === 'failed') {
      setState('failed');
      setError('A operação não chegou ao servidor. Reenvie em "Rascunhos e pendências".');
    } else if (result.kind === 'expired') {
      setState('failed');
      setError('Pendência antiga: confira no histórico se ela entrou.');
    }
  }, [queryClient]);

  // 08 §8.5 passo 4: resolve na hora se online, e de novo a cada reconexão.
  useEffect(() => {
    if (online && state === 'unknown') {
      void resolve();
    }
  }, [online, state, resolve]);

  const run = useCallback(
    async (vars: TVars): Promise<CriticalOutcome<TResult>> => {
      const o = optionsRef.current;
      if (!tenantId || !employeeId) throw new Error('Sem sessão.');
      // Resultado desconhecido nesta tela: confirmar de novo nunca gera outra chave (02 §2.3, RN-009.9).
      if (unknownKey.current) {
        return { kind: 'blocked', op: (await pendingOpsRepo.get(unknownKey.current))! };
      }
      const request = o.request(vars);
      setState('sending');
      setError(null);
      setFieldErrors({});
      const outcome = await runCritical<TResult>({
        tenantId,
        employeeId,
        request,
        description: o.describe(vars),
        draftId: o.draftId,
        idempotent: o.idempotent,
      });
      switch (outcome.kind) {
        case 'confirmed': {
          setState('idle');
          o.onConfirmed?.(outcome.result.data, vars, outcome.result.version);
          const message = o.successMessage?.(outcome.result.data, vars);
          if (message) toast(message);
          break;
        }
        case 'rejected':
          setState('idle');
          setError(errorMessage(outcome.error));
          setFieldErrors(outcome.error.errors);
          o.onRejected?.(outcome.error, vars);
          break;
        case 'conflict':
          setState('idle');
          o.onConflict?.(outcome.current, outcome.error, vars);
          if (!o.onConflict) setError('O registro foi alterado por outra pessoa. Confira e refaça.');
          break;
        case 'unknown':
          if (outcome.key) {
            unknownKey.current = outcome.key;
            setPendingKey(outcome.key);
            setState('unknown');
          } else {
            // Só [M]: o If-Match impede aplicar duas vezes. Recarrega e deixa a pessoa conferir.
            setState('idle');
            invalidateForPath(queryClient, request.path);
            setError('Sem resposta do servidor. Confira se a alteração entrou e tente de novo.');
          }
          break;
        case 'blocked':
          // Pendência de antes (outra visita à tela ou app reaberto): acompanha a mesma chave.
          unknownKey.current = outcome.op.idempotencyKey;
          setPendingKey(outcome.op.idempotencyKey);
          setState(outcome.op.status === 'FAILED' ? 'failed' : 'unknown');
          setError(outcome.op.status === 'FAILED' ? 'Há uma operação igual não enviada. Veja em "Rascunhos e pendências".' : null);
          break;
      }
      return outcome;
    },
    [tenantId, employeeId, queryClient],
  );

  const reset = useCallback(() => {
    setState('idle');
    setError(null);
    setFieldErrors({});
  }, []);

  // `busy` cobre também o desconhecido e o não enviado: o botão de confirmar fica travado até haver desfecho.
  return { run, state, error, fieldErrors, reset, busy: state !== 'idle', pendingKey };
}
