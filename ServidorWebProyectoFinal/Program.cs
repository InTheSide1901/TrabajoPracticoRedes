using System.IO;              // Para leer archivos, crear carpetas y escribir logs
using System.Text;            // Para convertir texto a bytes (UTF-8) y viceversa
using System.Net;             // Para manejar direcciones IP (IPAddress, IPEndPoint)
using System.Net.Sockets;     // Para crear el servidor TCP y aceptar clientes
using System.IO.Compression;  // Para comprimir las respuestas con GZIP

// =========================================================================
// ETAPA 1: CONFIGURACIÓN INICIAL DEL SERVIDOR (Requisitos 3 y 4)
// =========================================================================


// Objeto "candado" para sincronizar la escritura en el archivo de logs
// cuando múltiples hilos intentan escribir al mismo tiempo.
object lockLog = new object();

int puerto = 0;       // Puerto por defecto
string archivos = ""; // Carpeta raíz por defecto
string archivoConfig = "config.txt";

// Leemos el archivo de configuración externo
if (File.Exists(archivoConfig))
{
    string[] lineasConfig = File.ReadAllLines(archivoConfig);

    foreach (string linea in lineasConfig)
    {
        if (string.IsNullOrWhiteSpace(linea) || linea.StartsWith("#"))
            continue;

        string[] partes = linea.Split('=', 2);
        if (partes.Length < 2) continue;

        string clave = partes[0].Trim().ToUpper();
        string valor = partes[1].Trim();

        if (clave == "PORT")
            puerto = int.Parse(valor);
        else if (clave == "ROOT")
            archivos = valor;
    }
}
else
{
    Console.WriteLine("AVISO: No se encontró config.txt. Usando valores por defecto.");
}

Directory.CreateDirectory(archivos);

Console.WriteLine($"[CONFIG] Puerto configurado: {puerto}");
Console.WriteLine($"[CONFIG] Carpeta raíz: {Path.GetFullPath(archivos)}");

// =========================================================================
// ETAPA 2: ENCENDIDO DEL SERVIDOR TCP Y CONCURRENCIA (Requisito 1)
// =========================================================================

TcpListener listener = new TcpListener(IPAddress.Any, puerto);
listener.Start();

Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"[SERVIDOR] Escuchando peticiones en el puerto {puerto}...");
Console.WriteLine($"[INFO] Entrá desde tu navegador a: http://localhost:{puerto}");
Console.WriteLine("--------------------------------------------------");

// Bucle infinito para aceptar un número indefinido de conexiones
while (true)
{
    // Espera de forma bloqueante hasta que un cliente se conecte
    Socket cliente = listener.AcceptSocket();

    // Atendemos cada cliente en un hilo paralelo para no bloquear el servidor
    _ = Task.Run(() => AtenderCliente(cliente, archivos, lockLog));
}

// =========================================================================
// ETAPAS 3 Y 4: ATENCIÓN AL CLIENTE, LOGS, COMPRESIÓN Y RESPUESTA HTTP
// =========================================================================
static void AtenderCliente(Socket cliente, string archivos, object lockLog)
{
    using (cliente)
    {
        try
        {
            // 1. Obtener IP de origen (Requisito 9)
            string ipOrigen = ((IPEndPoint)cliente.RemoteEndPoint!).Address.ToString();

            // 2. Leer la petición HTTP enviada por el navegador
            byte[] buffer = new byte[8192];
            int bytesRecibidos = cliente.Receive(buffer);
            if (bytesRecibidos == 0) return;

            string request = Encoding.UTF8.GetString(buffer, 0, bytesRecibidos);
            string[] lineasReq = request.Split(new string[] { "\r\n" }, StringSplitOptions.None);//StringSplitOptions.None seria para no borrar los espacios en blanco \r\n
            if (lineasReq.Length == 0 || string.IsNullOrWhiteSpace(lineasReq[0])) return;
            //Es una medida defensiva para que el programa no explote si un cliente manda basura o espacios vacíos.
            //Pregunta: "¿El arreglo quedó vacío (Length == 0) o el primer renglón (lineasReq[0]) está en blanco?".
            //Si pasa eso, hace return (sale de AtenderCliente, cierra el socket por el using y evita que la línea siguiente lineasReq[0].Split(' ') tire un error).

            // 3. Extraer Método (GET/POST) y Ruta de la primera línea
            string primerLinea = lineasReq[0];
            string[] partesReq = primerLinea.Split(' ');
            if (partesReq.Length < 2) return;
            //El if (partesReq.Length < 2) return; es otra validación de seguridad:
            //si alguien manda un texto malformado sin espacios,
            //corta la ejecución para evitar un error de índice fuera de rango(IndexOutOfRangeException).

            string metodo = partesReq[0].ToUpper();
            string ruta = partesReq[1];

            string queryString = "";
            string postBody = "";

            // 4. Manejo de parámetros de consulta (Query String) en la URL (Requisito 7)
            if (ruta.Contains("?"))
            {
                string[] partesUrl = ruta.Split('?', 2);
                ruta = partesUrl[0];
                queryString = partesUrl[1];
            }

            // 5. Si la URL no especifica archivo ("/"), servir index.html por defecto (Requisito 2)
            if (ruta == "/")
            {
                ruta = "/index.html";
            }

            // 6. Capturar los datos enviados en el cuerpo si es una petición POST (Requisito 6)
            if (metodo == "POST")
            {
                int indiceVacio = Array.IndexOf(lineasReq, "");
                if (indiceVacio != -1 && indiceVacio + 1 < lineasReq.Length)
                {
                    postBody = string.Join("\r\n", lineasReq, indiceVacio + 1, lineasReq.Length - (indiceVacio + 1));
                }
                //string.Join:
                //              // 1. Con qué pegamento unirlos
                //              // 2. De qué arreglo sacarlos
                //              // 3. Desde qué posición empezar
                //              // 4. Cuántos renglones agarrar
            }

            // 7. Verificar si el navegador soporta compresión GZIP (Requisito 8)
            bool aceptaGzip = false;
            foreach (string linea in lineasReq)
            {
                if (linea.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    linea.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    aceptaGzip = true;
                    break;
                }
            }

            // 8. Registrar la solicitud en el archivo de log diario (Requisito 9)
            string dirLogs = "logs";
            Directory.CreateDirectory(dirLogs);
            string archivoLog = Path.Combine(dirLogs, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

            // Sanitizamos los datos recibidos para neutralizar caracteres peligrosos (<, >, ", &)
            string querySeguro = WebUtility.HtmlEncode(queryString);
            string postSeguro = WebUtility.HtmlEncode(postBody);

            string entradaLog = $"{DateTime.Now:HH:mm:ss} | IP: {ipOrigen} | {metodo} | Ruta: {ruta}";
            if (!string.IsNullOrEmpty(queryString)) entradaLog += $" | Query: {queryString}";
            if (!string.IsNullOrEmpty(postBody)) entradaLog += $" | POST Data: {postBody}";

            lock (lockLog)
            {
                File.AppendAllText(archivoLog, entradaLog + Environment.NewLine);
            }

            Console.WriteLine($"{entradaLog}");

            // 9. Seguridad: Prevención de Path Traversal (Evitar que salgan de la carpeta raíz)
            string rutaBase = Path.GetFullPath(archivos);
            if (!rutaBase.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                rutaBase += Path.DirectorySeparatorChar;
            }

            string rutaRelativa = ruta.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            string rutaCompleta = Path.GetFullPath(Path.Combine(rutaBase, rutaRelativa));

            if (!rutaCompleta.StartsWith(rutaBase, StringComparison.OrdinalIgnoreCase))
            {
                string html403 = "<!DOCTYPE html><html><head><meta charset='UTF-8'><title>403 - Prohibido</title></head>" +
                                 "<body><h1>403 - Acceso Denegado</h1><p>No tenés permiso para acceder a esta ruta.</p></body></html>";
                EnviarRespuesta(cliente, "403 Forbidden", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html403));
                return;
            }

            // 10. Buscar el archivo solicitado y enviarlo (Requisito 5 y 8)
            FileInfo archivo = new FileInfo(rutaCompleta);

            if (archivo.Exists)
            {
                // Leemos los bytes del archivo (HTML, CSS, JS, imagen, etc.)
                byte[] archivoBytes = File.ReadAllBytes(rutaCompleta);
                string tipoMime = ObtenerTipoMime(rutaCompleta);

                // Si el cliente acepta GZIP, comprimimos el archivo en memoria antes de enviarlo
                if (aceptaGzip)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (GZipStream gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            gzip.Write(archivoBytes, 0, archivoBytes.Length);
                        }
                        byte[] bytesComprimidos = ms.ToArray();
                        EnviarRespuesta(cliente, "200 OK", tipoMime, bytesComprimidos, "gzip");
                    }
                }
                else
                {
                    // Si no acepta GZIP, lo enviamos sin comprimir
                    EnviarRespuesta(cliente, "200 OK", tipoMime, archivoBytes);
                }
            }
            else
            {
                // Buscamos el documento personalizado 404.html en la carpeta raíz (Requisito 5)
                string ruta404 = Path.Combine(rutaBase, "404error.html");
                byte[] cuerpo404;

                if (File.Exists(ruta404))
                {
                    cuerpo404 = File.ReadAllBytes(ruta404);
                }
                else
                {
                    // Respaldo de emergencia por si no existe el archivo 404.html
                    string fallback = "<h1>404 - Archivo no encontrado</h1>";
                    cuerpo404 = Encoding.UTF8.GetBytes(fallback);
                }

                // Importante: aunque enviemos un HTML válido, el código de estado HTTP DEBE ser 404 Not Found
                EnviarRespuesta(cliente, "404 Not Found", "text/html; charset=utf-8", cuerpo404);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Fallo en la conexión con el cliente: {ex.Message}");
        }
    }
}

// =========================================================================
// FUNCIONES AUXILIARES PARA ENVIAR RESPUESTAS Y DETECTAR TIPOS DE ARCHIVO
// =========================================================================

// Arma los encabezados HTTP válidos y envía tanto los headers como el cuerpo (archivo) por el socket
static void EnviarRespuesta(Socket cliente, string codigoEstado, string contentType, byte[] cuerpo, string? contentEncoding = null)
{
    StringBuilder headers = new StringBuilder();
    headers.Append($"HTTP/1.1 {codigoEstado}\r\n");
    headers.Append($"Content-Type: {contentType}\r\n");
    headers.Append($"Content-Length: {cuerpo.Length}\r\n");

    // Si el contenido va comprimido con GZIP, debemos avisarle al navegador con este header
    if (!string.IsNullOrEmpty(contentEncoding))
    {
        headers.Append($"Content-Encoding: {contentEncoding}\r\n");
    }

    // Cabeceras de seguridad contra XSS y sniffing de archivos
    headers.Append("Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';\r\n");
    headers.Append("X-Content-Type-Options: nosniff\r\n");

    headers.Append("Connection: close\r\n");
    headers.Append("\r\n"); // Línea vacía obligatoria que separa los headers del archivo

    // Enviamos primero los encabezados en texto y luego los bytes del archivo
    cliente.Send(Encoding.UTF8.GetBytes(headers.ToString()));
    cliente.Send(cuerpo);
}

// Devuelve el Content-Type (MIME Type) correspondiente según la extensión del archivo
static string ObtenerTipoMime(string rutaArchivo)
{
    string ext = Path.GetExtension(rutaArchivo).ToLower();
    return ext switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };
}