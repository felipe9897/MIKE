# Project Dependencies (NuGet)

Since the build environment has restricted NuGet access, please ensure the following packages are installed in your local development environment.

## Mike.Common
- System.Security.Cryptography.ProtectedData (Used for DPAPI secrets)

## Mike.Desktop
- Microsoft.Web.WebView2 (Used for the UI host)

## Mike.Service
- Microsoft.Extensions.Hosting (Used for the Windows Background Service)

## Mike.Tray
- System.Windows.Forms (Via <UseWindowsForms>true</UseWindowsForms> in .csproj)
