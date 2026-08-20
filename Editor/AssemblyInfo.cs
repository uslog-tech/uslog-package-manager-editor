using System.Runtime.CompilerServices;

// テストからだけ internal を見せる。公開 API に出すほどではないが、
// 「展開先の外を弾く」ような要のロジックを外から確かめられなくするのは困る。
[assembly: InternalsVisibleTo("tech.uslog.package-manager.Editor.Tests")]
