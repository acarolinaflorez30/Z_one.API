-- =====================================================================================
-- tests/sql/reglas_bd.sql — pruebas automáticas de las reglas que vive la base de datos
--
-- Comprueba (cada prueba tiene su número y descripción): choques de sala, duraciones, límites del paquete,
-- transiciones de estado de sesiones/proyectos/cobros/invitaciones, expiración automática,
-- historial de precios, trazabilidad inmutable, fichas de sala y registro de avisos.
-- NO incluye nada de pasarela de pagos.
--
-- CÓMO EJECUTARLO: pega todo en el SQL Editor de Supabase (mejor en una BRANCH o base de pruebas) o con
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f tests/sql/reglas_bd.sql
-- Es un solo bloque DO. Crea sus propios datos de prueba y AL FINAL SE REVIERTE TODO a propósito:
-- termina con un "error" cuyo mensaje es el resultado:
--     PRUEBAS OK: 52/52 ...      -> todo bien
--     PRUEBAS FALLIDAS: ...      -> lista cuáles fallaron
-- No deja datos. (Solo consume algunos números de identidad, sin consecuencia.)
-- Requisitos: haber ejecutado antes los SQL 13 a 17.
-- =====================================================================================

drop table if exists pg_temp._pruebas;
create temp table _pruebas (n serial, nombre text, ok boolean, detalle text);

create or replace function pg_temp.registrar(p_nombre text, p_ok boolean, p_detalle text default null) returns void
language plpgsql as $$
begin
  insert into _pruebas (nombre, ok, detalle) values (p_nombre, coalesce(p_ok, false), p_detalle);
end $$;

-- Debe fallar con un mensaje que contenga p_patron. Siempre deshace lo que haga el SQL.
create or replace function pg_temp.espera_error(p_nombre text, p_sql text, p_patron text) returns void
language plpgsql as $$
declare v_msg text;
begin
  begin
    execute p_sql;
    raise exception 'SIN_ERROR_ESPERADO';
  exception when others then
    v_msg := sqlerrm;
  end;
  perform pg_temp.registrar(p_nombre, v_msg <> 'SIN_ERROR_ESPERADO' and v_msg ilike '%' || p_patron || '%',
    case when v_msg = 'SIN_ERROR_ESPERADO' then 'debía fallar y no falló' else 'mensaje: ' || left(v_msg, 160) end);
end $$;

-- Debe ejecutarse sin error (y deja el cambio para las pruebas siguientes).
create or replace function pg_temp.espera_ok(p_nombre text, p_sql text) returns void
language plpgsql as $$
begin
  execute p_sql;
  perform pg_temp.registrar(p_nombre, true);
exception when others then
  perform pg_temp.registrar(p_nombre, false, 'falló: ' || left(sqlerrm, 160));
end $$;

do $prueba$
declare
  v_tag text := 'zt_' || substr(md5(random()::text), 1, 8);
  u_admin uuid; u_cli uuid; u_col1 uuid; u_col2 uuid; c_cli uuid;
  v_sala1 bigint; v_sala2 bigint; v_srv_p bigint; v_srv_c bigint;
  v_pp bigint; v_pc bigint; v_pe bigint; v_px bigint;           -- proyectos: parcial, canción completa, para expiración
  v_s1 bigint; v_s2 bigint; v_s3 bigint; v_s4 bigint; v_s5 bigint; v_s6 bigint; v_s7 bigint;
  v_o1 bigint; v_o2 bigint; v_o3 bigint;
  v_n int; v_txt text; v_ok boolean;
  v_base timestamptz := date_trunc('hour', now()) + interval '30 days';
  v_fallos text; v_total int; v_buenas int;
begin
  -- ───────── Datos de prueba ─────────
  insert into public.usuarios (email, rol, nombre) values (v_tag || '_admin@prueba.test', 'administrador', 'Admin prueba') returning id into u_admin;
  insert into public.usuarios (email, rol, nombre) values (v_tag || '_cli@prueba.test', 'cliente', 'Cliente prueba') returning id into u_cli;
  insert into public.usuarios (email, rol, nombre) values (v_tag || '_c1@prueba.test', 'colaborador', 'Colab 1') returning id into u_col1;
  insert into public.usuarios (email, rol, nombre) values (v_tag || '_c2@prueba.test', 'colaborador', 'Colab 2') returning id into u_col2;
  insert into public.clientes (usuario_id, nombre) values (u_cli, 'Cliente prueba') returning id into c_cli;
  insert into public.salas (nombre, precio_hora, precio_sesion) values (v_tag || ' Sala A', 50000, 300000) returning id into v_sala1;
  insert into public.salas (nombre, precio_hora, precio_sesion) values (v_tag || ' Sala B', 40000, 250000) returning id into v_sala2;
  insert into public.servicios (codigo, nombre, categoria, sesiones_incluidas) values (v_tag || '_p', 'Parcial prueba', 'servicio_parcial', 1) returning id into v_srv_p;
  insert into public.servicios (codigo, nombre, categoria, sesiones_incluidas) values (v_tag || '_c', 'Canción prueba', 'cancion_completa', 4) returning id into v_srv_c;
  insert into public.proyectos (cliente_id, servicio_id, titulo, tipo, sesiones_incluidas)
    values (c_cli, v_srv_p, 'Parcial prueba', 'servicio_parcial', 1) returning id into v_pp;
  insert into public.proyectos (cliente_id, servicio_id, titulo, tipo, sesiones_incluidas)
    values (c_cli, v_srv_c, 'Canción prueba', 'cancion_completa', 4) returning id into v_pc;

  -- ───────── 1. Choques de sala y duración ─────────
  insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
    values ('alquiler', v_sala1, c_cli, v_base, v_base + interval '2 hours', 'pendiente_pago', v_base) returning id into v_s1;
  perform pg_temp.espera_error('1.1 Dos reservas que se cruzan en la misma sala no se permiten',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago', %L)$q$, v_sala1, c_cli, v_base + interval '1 hour', v_base + interval '3 hours', v_base),
    'sesiones_sin_choque_de_sala');
  perform pg_temp.espera_ok('1.2 Reservas seguidas (una termina cuando empieza la otra) sí se permiten',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago', %L)$q$, v_sala1, c_cli, v_base + interval '2 hours', v_base + interval '3 hours', v_base));
  perform pg_temp.espera_ok('1.3 Mismo horario en otra sala sí se permite',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago', %L)$q$, v_sala2, c_cli, v_base, v_base + interval '2 hours', v_base));
  perform pg_temp.espera_error('1.4 Un alquiler de más de 4 horas no se permite',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago', %L)$q$, v_sala2, c_cli, v_base + interval '1 day', v_base + interval '1 day 5 hours', v_base),
    'sesiones_alquiler_max_4h');
  perform pg_temp.espera_error('1.5 Una reserva no puede terminar antes de empezar',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago', %L)$q$, v_sala2, c_cli, v_base + interval '2 days', v_base + interval '2 days' - interval '1 hour', v_base),
    'sesiones_fin_despues_inicio');
  perform pg_temp.espera_error('1.6 Una reserva pendiente debe tener plazo de vencimiento',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago')$q$, v_sala2, c_cli, v_base + interval '3 days', v_base + interval '3 days 1 hour'),
    'sesiones_pendiente_con_plazo');
  perform pg_temp.espera_error('1.7 Una reserva nueva no puede nacer ya confirmada',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado)
      values ('alquiler', %s, %L, %L, %L, 'confirmada')$q$, v_sala2, c_cli, v_base + interval '4 days', v_base + interval '4 days 1 hour'),
    'debe iniciar pendiente');
  perform pg_temp.espera_error('1.8 Un alquiler no puede llevar proyecto',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, proyecto_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %s, %L, %L, 'pendiente_pago', %L)$q$, v_sala2, c_cli, v_pp, v_base + interval '5 days', v_base + interval '5 days 1 hour', v_base),
    'sesiones_campos_por_tipo');

  -- Una reserva vencida/cancelada libera la sala
  update public.sesiones set estado = 'cancelada' where id = v_s1;
  perform pg_temp.espera_ok('1.9 Una reserva cancelada libera la sala',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago', %L)$q$, v_sala1, c_cli, v_base, v_base + interval '1 hour', v_base));

  -- ───────── 2. Paquete del proyecto ─────────
  insert into public.sesiones (tipo, sala_id, proyecto_id, numero_sesion, etapa, inicio, fin, estado, expira_en)
    values ('proyecto', v_sala2, v_pp, 1, 'grabacion', v_base + interval '10 days', v_base + interval '10 days 3 hours', 'pendiente_aceptacion', v_base)
    returning id into v_s2;
  perform pg_temp.espera_error('2.1 Un servicio parcial de 1 sesión no admite una segunda sesión de trabajo',
    format($q$insert into public.sesiones (tipo, sala_id, proyecto_id, numero_sesion, etapa, inicio, fin, estado, expira_en)
      values ('proyecto', %s, %s, 2, 'grabacion', %L, %L, 'pendiente_aceptacion', %L)$q$, v_sala2, v_pp, v_base + interval '11 days', v_base + interval '11 days 3 hours', v_base),
    'todas las sesiones incluidas');
  perform pg_temp.espera_error('2.2 Ni siquiera una sesión extra se abre mientras el proyecto tiene otra sesión abierta',
    format($q$insert into public.sesiones (tipo, sala_id, proyecto_id, es_extra, inicio, fin, estado, expira_en)
      values ('proyecto', %s, %s, true, %L, %L, 'pendiente_pago', %L)$q$, v_sala2, v_pp, v_base + interval '12 days', v_base + interval '12 days 2 hours', v_base),
    'sesiones_una_abierta_por_proyecto');
  perform pg_temp.espera_error('2.3 El ajuste incluido no puede llevar número de sesión',
    format($q$insert into public.sesiones (tipo, sala_id, proyecto_id, numero_sesion, etapa, inicio, fin, estado, expira_en)
      values ('proyecto', %s, %s, 1, 'revision', %L, %L, 'pendiente_aceptacion', %L)$q$, v_sala2, v_pc, v_base + interval '13 days', v_base + interval '13 days 3 hours', v_base),
    'sesiones_ajuste_sin_numero');
  perform pg_temp.espera_ok('2.4 La sesión 1 de una canción completa se crea',
    format($q$insert into public.sesiones (tipo, sala_id, proyecto_id, numero_sesion, etapa, inicio, fin, estado, expira_en)
      values ('proyecto', %s, %s, 1, 'produccion', %L, %L, 'pendiente_aceptacion', %L)$q$, v_sala1, v_pc, v_base + interval '14 days', v_base + interval '14 days 3 hours', v_base));
  perform pg_temp.espera_error('2.5 Un proyecto no puede tener dos sesiones de trabajo abiertas a la vez',
    format($q$insert into public.sesiones (tipo, sala_id, proyecto_id, numero_sesion, etapa, inicio, fin, estado, expira_en)
      values ('proyecto', %s, %s, 2, 'grabacion', %L, %L, 'pendiente_aceptacion', %L)$q$, v_sala1, v_pc, v_base + interval '15 days', v_base + interval '15 days 3 hours', v_base),
    'sesiones_una_abierta_por_proyecto');

  -- ───────── 3. Estados de sesión ─────────
  perform pg_temp.espera_error('3.1 Una sesión con cobro no se confirma sin pasar por el pago',
    format('update public.sesiones set estado = %L where id = %s', 'confirmada', v_s2), 'debe pasar por el pago');
  update public.sesiones set estado = 'pendiente_pago', expira_en = v_base where id = v_s2;
  perform pg_temp.espera_ok('3.2 Pendiente de pago pasa a confirmada', format('update public.sesiones set estado = %L where id = %s', 'confirmada', v_s2));
  perform pg_temp.espera_error('3.3 Una sesión confirmada no se puede cancelar',
    format('update public.sesiones set estado = %L where id = %s', 'cancelada', v_s2), 'No se puede cancelar una sesion confirmada');
  perform pg_temp.espera_error('3.4 Una sesión confirmada no se puede reprogramar',
    format('update public.sesiones set inicio = inicio + interval %L, fin = fin + interval %L where id = %s', '1 hour', '1 hour', v_s2), 'no se puede reprogramar');
  perform pg_temp.espera_error('3.5 No se puede marcar cumplida antes de que termine su horario',
    format('update public.sesiones set estado = %L, cumplida_en = now(), cumplida_por = %L, resena = %L where id = %s', 'cumplida', u_admin, 'ok', v_s2),
    'solo se puede marcar como cumplida cuando termina');

  -- Sesión extra del pasado, en otro proyecto, para cerrarla
  insert into public.proyectos (cliente_id, servicio_id, titulo, tipo, sesiones_incluidas)
    values (c_cli, v_srv_p, 'Extra prueba', 'servicio_parcial', 1) returning id into v_px;
  insert into public.sesiones (tipo, sala_id, proyecto_id, es_extra, inicio, fin, estado, expira_en)
    values ('proyecto', v_sala1, v_px, true, now() - interval '3 days', now() - interval '3 days' + interval '2 hours', 'pendiente_pago', now() + interval '1 hour')
    returning id into v_s3;
  update public.sesiones set estado = 'confirmada' where id = v_s3;
  perform pg_temp.espera_error('3.6 Cumplida exige reseña',
    format('update public.sesiones set estado = %L, cumplida_en = now(), cumplida_por = %L where id = %s', 'cumplida', u_admin, v_s3), 'sesiones_cumplida_con_resena');
  perform pg_temp.espera_ok('3.7 Una sesión terminada se marca cumplida con reseña',
    format('update public.sesiones set estado = %L, cumplida_en = now(), cumplida_por = %L, resena = %L where id = %s', 'cumplida', u_admin, 'Se grabó bien', v_s3));
  perform pg_temp.espera_error('3.8 Una sesión cumplida ya no se puede modificar',
    format('update public.sesiones set estado = %L where id = %s', 'cancelada', v_s3), 'estado final');

  -- ───────── 4. Cobros ─────────
  insert into public.ordenes (concepto, cliente_id, sesion_id, monto, vence_en)
    values ('servicio', c_cli, v_s2, 300000, now() + interval '24 hours') returning id into v_o1;
  perform pg_temp.espera_error('4.1 Una sesión no puede tener dos cobros vigentes',
    format($q$insert into public.ordenes (concepto, cliente_id, sesion_id, monto) values ('servicio', %L, %s, 1000)$q$, c_cli, v_s2),
    'ordenes_sesion_vigente_unica');
  perform pg_temp.espera_error('4.2 El monto de un cobro no puede ser negativo',
    format($q$insert into public.ordenes (concepto, cliente_id, proyecto_id, monto) values ('servicio', %L, %s, -1)$q$, c_cli, v_pp), 'ordenes_monto_check');
  perform pg_temp.espera_error('4.3 Un cobro pagado debe tener quién lo registró y cuándo',
    format('update public.ordenes set estado = %L where id = %s', 'pagada', v_o1), 'ordenes_pagada_con_registro');
  perform pg_temp.espera_ok('4.4 Un cobro vigente se registra como pagado',
    format('update public.ordenes set estado = %L, pagada_en = now(), registrada_por = %L where id = %s', 'pagada', u_admin, v_o1));
  perform pg_temp.espera_error('4.5 Un cobro pagado no se puede modificar ni reembolsar',
    format('update public.ordenes set estado = %L, pagada_en = null, registrada_por = null where id = %s', 'pendiente', v_o1), 'pagada no se puede');
  insert into public.ordenes (concepto, cliente_id, proyecto_id, monto, vence_en)
    values ('servicio', c_cli, v_pc, 1000, now() - interval '1 minute') returning id into v_o2;
  perform pg_temp.espera_error('4.6 No se puede registrar el pago después del plazo',
    format('update public.ordenes set estado = %L, pagada_en = now(), registrada_por = %L where id = %s', 'pagada', u_admin, v_o2), 'plazo de 24 horas');
  update public.ordenes set estado = 'expirada' where id = v_o2;
  perform pg_temp.espera_error('4.7 Un cobro expirado no se reactiva',
    format('update public.ordenes set estado = %L where id = %s', 'pendiente', v_o2), 'no se puede reactivar');

  -- ───────── 5. Invitaciones ─────────
  insert into public.aceptaciones_sesion (sesion_id, usuario_id, rol) values (v_s2, u_col1, 'artista');
  insert into public.aceptaciones_sesion (sesion_id, usuario_id, rol) values (v_s2, u_col2, 'productor');
  perform pg_temp.espera_error('5.1 La misma persona no se invita dos veces a la misma sesión',
    format($q$insert into public.aceptaciones_sesion (sesion_id, usuario_id, rol) values (%s, %L, 'artista')$q$, v_s2, u_col1), 'aceptaciones_sesion_pkey');
  perform pg_temp.espera_error('5.2 Una respuesta debe llevar fecha de respuesta',
    format($q$update public.aceptaciones_sesion set estado = 'aceptada' where sesion_id = %s and usuario_id = %L$q$, v_s2, u_col1), 'aceptaciones_respuesta_coherente');
  perform pg_temp.espera_ok('5.3 Un colaborador acepta su invitación',
    format($q$update public.aceptaciones_sesion set estado = 'aceptada', respondida_en = now() where sesion_id = %s and usuario_id = %L$q$, v_s2, u_col1));
  perform pg_temp.espera_error('5.4 Una respuesta ya dada no se puede cambiar',
    format($q$update public.aceptaciones_sesion set estado = 'rechazada' where sesion_id = %s and usuario_id = %L$q$, v_s2, u_col1), 'ya fue respondida');
  perform pg_temp.espera_error('5.5 El rol de una invitación no se puede cambiar',
    format($q$update public.aceptaciones_sesion set rol = 'artista' where sesion_id = %s and usuario_id = %L$q$, v_s2, u_col2), 'no se puede cambiar');

  -- ───────── 6. Proyectos ─────────
  perform pg_temp.espera_error('6.1 El precio acordado de un proyecto no se puede cambiar',
    format('update public.proyectos set precio_acordado = 5 where id = %s', v_pc), 'no se pueden cambiar');
  perform pg_temp.espera_error('6.2 Un proyecto pendiente no salta a "en revisión"',
    format($q$update public.proyectos set estado = 'en_revision', revision_vence_en = now() + interval '1 day' where id = %s$q$, v_pc), 'Transicion de proyecto no permitida');
  update public.proyectos set estado = 'confirmado' where id = v_pp;
  perform pg_temp.espera_error('6.3 Un proyecto confirmado ya no se puede cancelar',
    format($q$update public.proyectos set estado = 'cancelado', cancelado_en = now() where id = %s$q$, v_pp), 'ya no se puede cancelar');
  perform pg_temp.espera_error('6.4 "En revisión" exige un plazo de revisión',
    format($q$update public.proyectos set estado = 'en_curso' where id = %s$q$, v_pp) || '; ' ||
    format($q$update public.proyectos set estado = 'en_revision' where id = %s$q$, v_pp), 'Transicion de proyecto no permitida');
  perform pg_temp.espera_error('6.5 Un servicio parcial no pasa por revisión (solo la canción completa)',
    format($q$update public.proyectos set estado = 'en_revision', revision_vence_en = now() + interval '1 day' where id = %s$q$, v_pp), 'Transicion de proyecto no permitida');

  -- ───────── 7. Expiración automática ─────────
  -- Proyecto pendiente cuya única sesión venció: debe quedar cancelado
  insert into public.proyectos (cliente_id, servicio_id, titulo, tipo, sesiones_incluidas)
    values (c_cli, v_srv_p, 'Se vence', 'servicio_parcial', 1) returning id into v_pe;
  insert into public.sesiones (tipo, sala_id, proyecto_id, numero_sesion, etapa, inicio, fin, estado, expira_en)
    values ('proyecto', v_sala2, v_pe, 1, 'grabacion', v_base + interval '20 days', v_base + interval '20 days 3 hours', 'pendiente_aceptacion', now() - interval '1 minute')
    returning id into v_s4;
  insert into public.aceptaciones_sesion (sesion_id, usuario_id, rol) values (v_s4, u_col1, 'artista');
  -- Sesión de alquiler con cobro pendiente vencido
  insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
    values ('alquiler', v_sala2, c_cli, v_base + interval '21 days', v_base + interval '21 days 2 hours', 'pendiente_pago', now() - interval '1 minute')
    returning id into v_s5;
  insert into public.ordenes (concepto, cliente_id, sesion_id, monto, vence_en)
    values ('alquiler', c_cli, v_s5, 100000, now() - interval '1 minute') returning id into v_o3;
  -- Sesión vigente que NO debe tocarse
  insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
    values ('alquiler', v_sala2, c_cli, v_base + interval '22 days', v_base + interval '22 days 2 hours', 'pendiente_pago', now() + interval '10 hours')
    returning id into v_s6;

  perform public.expirar_vencidos();

  select estado::text into v_txt from public.sesiones where id = v_s4;
  perform pg_temp.registrar('7.1 La sesión sin respuesta a tiempo queda expirada', v_txt = 'expirada', 'estado=' || v_txt);
  select estado::text into v_txt from public.aceptaciones_sesion where sesion_id = v_s4 and usuario_id = u_col1;
  perform pg_temp.registrar('7.2 Las invitaciones sin responder de esa sesión quedan expiradas', v_txt = 'expirada', 'estado=' || v_txt);
  select estado::text into v_txt from public.proyectos where id = v_pe;
  perform pg_temp.registrar('7.3 El proyecto pendiente sin sesión viva queda cancelado', v_txt = 'cancelado', 'estado=' || v_txt);
  select estado::text into v_txt from public.sesiones where id = v_s5;
  perform pg_temp.registrar('7.4 La reserva sin pago a tiempo queda expirada', v_txt = 'expirada', 'estado=' || v_txt);
  select estado::text into v_txt from public.ordenes where id = v_o3;
  perform pg_temp.registrar('7.5 El cobro vencido queda expirado', v_txt = 'expirada', 'estado=' || v_txt);
  select estado::text into v_txt from public.sesiones where id = v_s6;
  perform pg_temp.registrar('7.6 Una reserva dentro de plazo NO se toca', v_txt = 'pendiente_pago', 'estado=' || v_txt);
  select estado::text into v_txt from public.proyectos where id = v_pp;
  perform pg_temp.registrar('7.7 Un proyecto confirmado NO se cancela por expirar', v_txt = 'confirmado', 'estado=' || v_txt);
  perform pg_temp.espera_ok('7.8 La sala de una reserva expirada queda libre',
    format($q$insert into public.sesiones (tipo, sala_id, cliente_id, inicio, fin, estado, expira_en)
      values ('alquiler', %s, %L, %L, %L, 'pendiente_pago', %L)$q$, v_sala2, c_cli, v_base + interval '21 days', v_base + interval '21 days 2 hours', v_base));
  select count(*) into v_n from (select public.expirar_vencidos()) x;
  perform pg_temp.registrar('7.9 Ejecutarla dos veces seguidas no rompe nada', v_n = 1);

  -- ───────── 8. Trazabilidad ─────────
  select count(*) into v_n from public.proyecto_historial where proyecto_id = v_pp;
  perform pg_temp.registrar('8.1 El historial del proyecto registra lo que pasó', v_n >= 3, 'filas=' || v_n);
  select count(*) into v_n from public.proyecto_historial where proyecto_id = v_pe and estado_nuevo = 'cancelado';
  perform pg_temp.registrar('8.2 El historial registra la cancelación automática', v_n >= 1, 'filas=' || v_n);
  perform pg_temp.espera_error('8.3 El historial del proyecto no se puede modificar',
    format($q$update public.proyecto_historial set evento = 'x' where proyecto_id = %s$q$, v_pp), 'solo');
  perform pg_temp.espera_error('8.4 El historial del proyecto no se puede borrar',
    format('delete from public.proyecto_historial where proyecto_id = %s', v_pp), 'solo');
  insert into public.auditoria (accion, entidad) values ('prueba', 'prueba');
  perform pg_temp.espera_error('8.5 La auditoría no se puede modificar', $q$update public.auditoria set accion = 'x' where accion = 'prueba'$q$, 'solo');
  perform pg_temp.espera_error('8.6 La auditoría no se puede borrar', $q$delete from public.auditoria where accion = 'prueba'$q$, 'solo');

  -- ───────── 9. Historial de precios ─────────
  select count(*) into v_n from public.historial_precios where tabla = 'salas' and registro_id = v_sala1;
  perform pg_temp.registrar('9.1 Crear una sala deja su precio inicial en el historial (hora y sesión)', v_n = 2, 'filas=' || v_n);
  update public.salas set precio_hora = 60000 where id = v_sala1;
  select count(*) into v_n from public.historial_precios where tabla = 'salas' and registro_id = v_sala1 and tipo_precio = 'hora' and precio_anterior = 50000 and precio_nuevo = 60000;
  perform pg_temp.registrar('9.2 Cambiar el precio por hora lo deja en el historial con anterior y nuevo', v_n = 1, 'filas=' || v_n);
  select count(*) into v_n from public.historial_precios where tabla = 'salas' and registro_id = v_sala1;
  update public.salas set nombre = nombre || ' (editada)' where id = v_sala1;
  perform pg_temp.registrar('9.3 Cambiar otra cosa (nombre) no agrega filas al historial',
    (select count(*) from public.historial_precios where tabla = 'salas' and registro_id = v_sala1) = v_n);
  update public.salas set precio_sesion = 320000 where id = v_sala1;
  select count(*) into v_n from public.historial_precios where tabla = 'salas' and registro_id = v_sala1 and tipo_precio = 'sesion' and precio_nuevo = 320000;
  perform pg_temp.registrar('9.4 Cambiar el precio por sesión también queda registrado', v_n = 1);
  perform pg_temp.espera_error('9.5 El historial de precios no se puede borrar',
    format($q$delete from public.historial_precios where tabla = 'salas' and registro_id = %s$q$, v_sala1), 'solo insercion');
  perform pg_temp.espera_error('9.6 El historial de precios no se puede modificar',
    format($q$update public.historial_precios set precio_nuevo = 1 where tabla = 'salas' and registro_id = %s$q$, v_sala1), 'solo insercion');

  -- ───────── 10. Fichas de sala ─────────
  select publicada into v_ok from public.salas where id = v_sala2;
  perform pg_temp.registrar('10.1 Una sala nueva nace sin publicar', v_ok = false);
  perform pg_temp.espera_error('10.2 Tratamiento acústico inválido se rechaza',
    format($q$update public.salas set tratamiento_acustico = 'rara' where id = %s$q$, v_sala2), 'salas_tratamiento_valido');
  perform pg_temp.espera_error('10.3 Tipo de uso inválido se rechaza',
    format($q$update public.salas set tipos_uso = array['volar'] where id = %s$q$, v_sala2), 'salas_tipos_uso_validos');
  perform pg_temp.espera_error('10.4 Consola que no es analógica ni digital se rechaza',
    format($q$update public.salas set consola_tipo = 'otra' where id = %s$q$, v_sala2), 'salas_consola_tipo_valido');
  perform pg_temp.espera_error('10.5 Capacidad cero o negativa se rechaza',
    format('update public.salas set capacidad_max = 0 where id = %s', v_sala2), 'salas_medidas_positivas');
  perform pg_temp.espera_error('10.6 Precio negativo se rechaza',
    format('update public.salas set precio_hora = -1 where id = %s', v_sala2), 'salas_precio_no_negativo');
  perform pg_temp.espera_error('10.7 Dos salas no pueden llamarse igual',
    format($q$insert into public.salas (nombre, precio_hora) select nombre, 1 from public.salas where id = %s$q$, v_sala2), 'salas');
  perform pg_temp.espera_ok('10.8 Una foto de la galería se guarda',
    format($q$insert into public.sala_medios (sala_id, tipo, clave, titulo) values (%s, 'foto', 'k/' || %L, 'Cabina')$q$, v_sala2, v_tag));
  perform pg_temp.espera_error('10.9 Un enlace de audio solo se acepta con https',
    format($q$insert into public.sala_medios (sala_id, tipo, url, titulo) values (%s, 'audio', 'http://x.test/a.mp3', 'Muestra')$q$, v_sala2), 'sala_medios');
  perform pg_temp.espera_ok('10.10 Un enlace de audio https se guarda',
    format($q$insert into public.sala_medios (sala_id, tipo, url, titulo) values (%s, 'audio', 'https://x.test/a.mp3', 'Muestra')$q$, v_sala2));

  -- ───────── 11. Avisos y correos ─────────
  insert into public.avisos_enviados (clave) values (v_tag || ':1:x');
  perform pg_temp.espera_error('11.1 El mismo aviso no se registra dos veces (no se envía duplicado)',
    format($q$insert into public.avisos_enviados (clave) values (%L)$q$, v_tag || ':1:x'), 'avisos_enviados_pkey');
  perform pg_temp.espera_error('11.2 Un correo pendiente con estado inventado se rechaza',
    format($q$insert into public.correos_pendientes (usuario_id, tipo, asunto, mensaje, estado) values (%L, 't', 'a', 'm', 'raro')$q$, u_cli), 'correos_pendientes');
  perform pg_temp.espera_ok('11.3 Un correo pendiente válido se encola',
    format($q$insert into public.correos_pendientes (usuario_id, tipo, asunto, mensaje) values (%L, 't', 'a', 'm')$q$, u_cli));
  select recibir_correos into v_ok from public.usuarios where id = u_cli;
  perform pg_temp.registrar('11.4 Por defecto las personas reciben correos', v_ok = true);

  -- ───────── Resultado ─────────
  select count(*), count(*) filter (where ok), string_agg(nombre || ' [' || coalesce(detalle, '') || ']', E'\n' order by n) filter (where not ok)
    into v_total, v_buenas, v_fallos from _pruebas;
  if v_total = v_buenas then
    raise exception 'PRUEBAS OK: %/% (todo se revirtió a propósito; no quedó ningún dato)', v_buenas, v_total;
  else
    raise exception E'PRUEBAS FALLIDAS: %/% ok. Fallan:\n%', v_buenas, v_total, v_fallos;
  end if;
end
$prueba$;
