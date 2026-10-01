using System.Configuration;
using System.Net.Mail;

namespace LegacyShop.Core.Services
{
    public interface IEmailService
    {
        void Enviar(string para, string assunto, string corpo);
    }

    public class EmailService : IEmailService
    {
        public void Enviar(string para, string assunto, string corpo)
        {
            using (var cliente = new SmtpClient())
            {
                var mensagem = new MailMessage(ConfigurationManager.AppSettings["Smtp.From"], para, assunto, corpo)
                {
                    IsBodyHtml = true
                };
                cliente.Send(mensagem);
            }
        }
    }
}
