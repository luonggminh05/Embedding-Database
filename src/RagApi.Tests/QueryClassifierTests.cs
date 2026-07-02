using Xunit;
using RagApi.Services;

namespace RagApi.Tests
{
    public class QueryClassifierTests
    {
        [Theory]
        // Task Instruction tests
        [InlineData("th\u00eam t\u1edd tr\u00ecnh m\u1edbi", QueryIntent.TaskInstruction)]
        [InlineData("th\u00eam m\u1edbi t\u1edd tr\u00ecnh b\u1eb1ng c\u00e1ch n\u00e0o", QueryIntent.TaskInstruction)]
        [InlineData("t\u1ea1o m\u1edbi h\u1ed3 s\u01a1", QueryIntent.TaskInstruction)]
        [InlineData("x\u00f3a t\u1edd tr\u00ecnh", QueryIntent.TaskInstruction)]
        [InlineData("duy\u1ec7t h\u1ed3 s\u01a1", QueryIntent.TaskInstruction)]
        [InlineData("\u0111\u1ec3 \u0111\u00ednh k\u00e8m h\u00ecnh \u1ea3nh trong t\u1edd tr\u00ecnh th\u00ec l\u00e0m nh\u1eefng g\u00ec", QueryIntent.TaskInstruction)]
        // UI Location tests
        [InlineData("n\u00fat l\u01b0u \u1edf \u0111\u00e2u", QueryIntent.UiLocation)]
        [InlineData("menu t\u1edd tr\u00ecnh", QueryIntent.UiLocation)]
        [InlineData("m\u00e0n h\u00ecnh g\u1ed3m nh\u1eefng g\u00ec", QueryIntent.UiLocation)]
        // General / Ambiguous tests
        [InlineData("thao t\u00e1c n\u00e0y n\u1eb1m \u1edf \u0111\u00e2u", QueryIntent.General)] // contains both (thao tac & o dau)
        [InlineData("ch\u01b0\u01a1ng tr\u00ecnh \u0111\u00e0o t\u1ea1o n\u00e0y c\u00f3 n\u1ed9i dung g\u00ec", QueryIntent.General)] // 'tạo' is in 'đào tạo' (excluded)
        [InlineData("c\u00e2u h\u1ecfi kh\u00f4ng r\u00f5 intent", QueryIntent.General)]
        // Exclusions test for 'buoc' in 'bat buoc'
        // 'trường này có bắt buộc nhập không': 'bắt buộc' has 'buoc' which is excluded. 'trường' is a UI keyword.
        // So 'hasUi = true', 'hasTask = false' -> should be UiLocation.
        [InlineData("tr\u01b0\u1eddng n\u00e0y c\u00f3 b\u1eaft bu\u1ed9c nh\u1eadp kh\u00f4ng", QueryIntent.UiLocation)] 
        public void Classify_ReturnsExpectedIntent(string message, QueryIntent expectedIntent)
        {
            var actual = QueryClassifier.Classify(message);
            Assert.Equal(expectedIntent, actual);
        }

        [Theory]
        [InlineData("gui phe duyet to trinh nghiep vu", "gui_phe_duyet")]
        [InlineData("to trinh nghiep vu - phe duyet", "duyet")]
        [InlineData("to trinh nghiep vu - them moi", "them_moi")]
        [InlineData("to trinh nghiep vu - ban giao", "ban_giao")]
        [InlineData("to trinh nghiep vu - chia se", "chia_se")]
        [InlineData("to trinh nghiep vu - uy quyen", "uy_quyen")]
        [InlineData("to trinh nghiep vu - tu choi", "tu_choi")]
        [InlineData("to trinh nghiep vu - them ghi chu / in to trinh", "them_ghi_chu")]
        [InlineData("to trinh nghiep vu - them moi- dinh kem anh", "dinh_kem")]
        [InlineData("de dinh kem hinh anh trong to trinh thi lam nhung gi", "dinh_kem")]
        [InlineData("làm sao để xem chi tiết tờ trình", "tim_kiem")]
        public void ActionKindClassifier_ReturnsCanonicalAction(string text, string expected)
        {
            Assert.Equal(expected, ActionKindClassifier.DetermineActionKind(text));
        }

        [Theory]
        [InlineData("cac buoc them to trinh moi")]
        [InlineData("huong dan tim kiem to trinh")]
        [InlineData("luu nhap to trinh nhu the nao")]
        [InlineData("them ghi chu cho to trinh")]
        [InlineData("huong dan dinh kem anh vao to trinh")]
        public void ActionKindClassifier_DetectsClearActionIntent(string text)
        {
            Assert.True(ActionKindClassifier.HasActionIntent(text));
        }
    }
}
