namespace ZOne.Api.Domain;

// Los nombres se traducen a snake_case y coinciden con los enums de PostgreSQL:
// IngenieroSonido -> ingeniero_sonido, NoDisponible -> no_disponible, etc.
public enum RolUsuario { Administrador, Cliente, Colaborador }
public enum TipoCliente { Persona, Empresa }
public enum PerfilColaborador { Artista, Productor, IngenieroSonido, IngenieroMezcla, IngenieroMaster, Otro }
public enum EstadoVinculacion { Activo, Desvinculado }
public enum Disponibilidad { Disponible, NoDisponible }
public enum TipoToken { RecuperacionPassword, VerificacionEmail }
public enum EtapaProyecto { Produccion, Grabacion, Mezcla, Masterizacion, Revision }
public enum EstadoPortafolio { Finalizado, Activo }
