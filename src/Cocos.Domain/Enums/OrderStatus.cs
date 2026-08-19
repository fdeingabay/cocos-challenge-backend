namespace Cocos.Domain.Enums;

public enum OrderStatus
{
    /// <summary>Orden LIMIT viva en el libro, esperando ejecucion. Reserva fondos o nominales.</summary>
    New,

    /// <summary>Ejecutada parcialmente. Sigue viva por el remanente y lo sigue reservando.</summary>
    PartiallyFilled,

    /// <summary>Ejecutada por completo. Terminal.</summary>
    Filled,

    /// <summary>Rechazada por el mercado (fondos o tenencia insuficientes). Terminal, y se persiste.</summary>
    Rejected,

    /// <summary>Cancelada por el usuario. Terminal. Libera la reserva.</summary>
    Cancelled,

    /// <summary>Vencida al cierre de la jornada sin ejecutarse. Terminal. Libera la reserva.</summary>
    Expired
}
