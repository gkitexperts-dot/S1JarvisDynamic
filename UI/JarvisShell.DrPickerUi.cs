using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool DrPickerUiClassHandlerRegistered = RegisterDrPickerUiClassHandler();
        private bool _drPickerUiInitHooked;
        private bool _drPickerUiNavigationHooked;

        private static bool RegisterDrPickerUiClassHandler()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_DrPickerUiLoaded));
            return true;
        }

        private static void JarvisShell_DrPickerUiLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null)
                shell.EnsureDrPickerUiInstalled();
        }

        private void EnsureDrPickerUiInstalled()
        {
            try
            {
                if (webView == null)
                    return;

                if (webView.CoreWebView2 != null)
                {
                    EnsureDrPickerUiNavigationHook();
                    return;
                }

                if (_drPickerUiInitHooked)
                    return;

                _drPickerUiInitHooked = true;
                webView.CoreWebView2InitializationCompleted += WebView_DrPickerUiInitializationCompleted;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-picker-ui] install hook EXCEPTION: " + ex);
            }
        }

        private void WebView_DrPickerUiInitializationCompleted(object sender,
            CoreWebView2InitializationCompletedEventArgs e)
        {
            try
            {
                if (webView != null)
                    webView.CoreWebView2InitializationCompleted -= WebView_DrPickerUiInitializationCompleted;
                _drPickerUiInitHooked = false;

                if (!e.IsSuccess)
                    return;

                EnsureDrPickerUiNavigationHook();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-picker-ui] initialization-completed EXCEPTION: " + ex);
            }
        }

        private void EnsureDrPickerUiNavigationHook()
        {
            if (_drPickerUiNavigationHooked || webView == null || webView.CoreWebView2 == null)
                return;

            webView.CoreWebView2.NavigationCompleted += DrPickerUi_NavigationCompleted;
            _drPickerUiNavigationHooked = true;
        }

        private async void DrPickerUi_NavigationCompleted(object sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (!e.IsSuccess || webView == null || webView.CoreWebView2 == null)
                    return;

                string script = @"
(function(){
  var dz=document.getElementById('drDropzone');
  if(!dz||dz.dataset.jarvisPickerUi==='1')return;
  dz.dataset.jarvisPickerUi='1';

  dz.setAttribute('role','button');
  dz.setAttribute('aria-label','Άνοιγμα αρχείων');
  dz.setAttribute('tabindex','0');
  dz.title='Άνοιγμα αρχείων';
  dz.innerHTML=''
    +'<div style=""display:flex;align-items:center;justify-content:center;gap:10px;width:100%;"">'
    +  '<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" style=""width:22px;height:22px;flex:none;"">'
    +    '<path d=""M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z""></path>'
    +  '</svg>'
    +  '<span style=""font-size:15px;font-weight:650;letter-spacing:.1px;"">Άνοιγμα</span>'
    +'</div>'
    +'<div style=""margin-top:5px;font-size:11.5px;opacity:.72;"">Επίλεξε ένα ή περισσότερα αρχεία</div>';

  dz.style.width='100%';
  dz.style.minHeight='74px';
  dz.style.padding='15px 18px';
  dz.style.cursor='pointer';
  dz.style.userSelect='none';
  dz.style.borderStyle='solid';
  dz.style.display='flex';
  dz.style.flexDirection='column';
  dz.style.alignItems='center';
  dz.style.justifyContent='center';
  dz.classList.remove('dragover');

  dz.addEventListener('keydown',function(ev){
    if(ev.key==='Enter'||ev.key===' '){
      ev.preventDefault();
      dz.click();
    }
  });
})();";

                await webView.CoreWebView2.ExecuteScriptAsync(script);
                DebugLog.Log("[dr-picker-ui] wide Open button installed; DR upload is picker-first, drag/drop not required.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-picker-ui] navigation injection EXCEPTION: " + ex);
            }
        }
    }
}
