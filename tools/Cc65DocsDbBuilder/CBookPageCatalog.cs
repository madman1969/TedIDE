namespace Cc65DocsDbBuilder;

/// <summary>
/// The bundled pages of "The C Book" (Banahan, Brady &amp; Doran - https://publications.gbdirect.co.uk/c_book/),
/// grouped by chapter the same way <see cref="PageCatalog"/> groups the cc65 manuals - titles and
/// structure transcribed from the book's own table of contents. <see cref="PageEntry.FileName"/> is
/// the book-relative path under <c>SourceHtml/CBook/</c> (without the trailing <c>.html</c>), e.g.
/// <c>chapter5/pointers</c> - needed because filenames like <c>summary.html</c>/<c>exercises.html</c>
/// repeat in every chapter, so a bare basename (the scheme <see cref="PageCatalog"/> uses, where
/// every cc65 manual page is already globally unique) wouldn't be. Each chapter's own "index" page
/// (e.g. <c>chapter5/</c>) is deliberately not included here: it's pure navigation (a heading plus a
/// list of links to the entries already listed below) with nothing DocViewer's own category tree
/// doesn't already show - see <see cref="CBookHtmlToMarkdownConverter"/>'s own doc comment.
///
/// The trailing <see cref="Copyright"/> entry (the book's own copyright/disclaimer page) is what
/// satisfies that page's free-redistribution license: it requires acknowledging the original
/// authorship and copyright with a link back to https://publications.gbdirect.co.uk/c_book/copyright.html,
/// which the ingested page's own Markdown carries.
/// </summary>
public static class CBookPageCatalog
{
    public static readonly IReadOnlyList<PageCategory> Categories =
    [
        new PageCategory("Preface",
        [
            new PageEntry("preface/about", "About This Book"),
            new PageEntry("preface/the_success_of_c", "The Success of C"),
            new PageEntry("preface/standards", "Standards"),
            new PageEntry("preface/hosted_and_free_standing", "Hosted and Free-Standing Environments"),
            new PageEntry("preface/typographical_conventions", "Typographical Conventions"),
            new PageEntry("preface/order_of_topics", "Order of Topics"),
            new PageEntry("preface/example_programs", "Example Programs"),
            new PageEntry("preface/higher_authority", "Deference to Higher Authority"),
            new PageEntry("preface/c_standard", "Address for the Standard"),
        ]),
        new PageCategory("Chapter 1. An Introduction to C",
        [
            new PageEntry("chapter1/form_of_a_c_program", "1.1. The Form of a C Program"),
            new PageEntry("chapter1/functions", "1.2. Functions"),
            new PageEntry("chapter1/description_of_example", "1.3. A Description of Example 1.1"),
            new PageEntry("chapter1/some_more_programs", "1.4. Some More Programs"),
            new PageEntry("chapter1/terminology", "1.5. Terminology"),
            new PageEntry("chapter1/summary", "1.6. Summary"),
            new PageEntry("chapter1/exercises", "1.7. Exercises"),
        ]),
        new PageCategory("Chapter 2. Variables and Arithmetic",
        [
            new PageEntry("chapter2/fundamentals", "2.1. Some Fundamentals"),
            new PageEntry("chapter2/alphabet_of_c", "2.2. The Alphabet of C"),
            new PageEntry("chapter2/textual_program_structure", "2.3. The Textual Structure of Programs"),
            new PageEntry("chapter2/keywords_and_identifiers", "2.4. Keywords and Identifiers"),
            new PageEntry("chapter2/variable_declaration", "2.5. Declaration of Variables"),
            new PageEntry("chapter2/real_types", "2.6. Real Types"),
            new PageEntry("chapter2/integral_types", "2.7. Integral Types"),
            new PageEntry("chapter2/expressions_and_arithmetic", "2.8. Expressions and Arithmetic"),
            new PageEntry("chapter2/constants", "2.9. Constants"),
            new PageEntry("chapter2/summary", "2.10. Summary"),
            new PageEntry("chapter2/exercises", "2.11. Exercises"),
        ]),
        new PageCategory("Chapter 3. Control of Flow and Logical Expressions",
        [
            new PageEntry("chapter3/task_ahead", "3.1. The Task Ahead"),
            new PageEntry("chapter3/flow_control", "3.2. Control of Flow"),
            new PageEntry("chapter3/logical_expressions", "3.3. More Logical Expressions"),
            new PageEntry("chapter3/strange_operators", "3.4. Strange Operators"),
            new PageEntry("chapter3/summary", "3.5. Summary"),
            new PageEntry("chapter3/exercises", "3.6. Exercises"),
        ]),
        new PageCategory("Chapter 4. Functions",
        [
            new PageEntry("chapter4/changes", "4.1. Changes"),
            new PageEntry("chapter4/function_types", "4.2. The Type of Functions"),
            new PageEntry("chapter4/recursion_and_argument_passing", "4.3. Recursion and Argument Passing"),
            new PageEntry("chapter4/linkage", "4.4. Linkage"),
            new PageEntry("chapter4/summary", "4.5. Summary"),
            new PageEntry("chapter4/exercises", "4.6. Exercises"),
        ]),
        new PageCategory("Chapter 5. Arrays and Pointers",
        [
            new PageEntry("chapter5/opening_shots", "5.1. Opening Shots"),
            new PageEntry("chapter5/arrays", "5.2. Arrays"),
            new PageEntry("chapter5/pointers", "5.3. Pointers"),
            new PageEntry("chapter5/character_handling", "5.4. Character Handling"),
            new PageEntry("chapter5/sizeof_and_malloc", "5.5. Sizeof and Storage Allocation"),
            new PageEntry("chapter5/function_pointers", "5.6. Pointers to Functions"),
            new PageEntry("chapter5/pointer_expressions", "5.7. Expressions Involving Pointers"),
            new PageEntry("chapter5/arrays_and_address_of", "5.8. Arrays, the & Operator and Function Declarations"),
            new PageEntry("chapter5/summary", "5.9. Summary"),
            new PageEntry("chapter5/exercises", "5.10. Exercises"),
        ]),
        new PageCategory("Chapter 6. Structured Data Types",
        [
            new PageEntry("chapter6/history", "6.1. History"),
            new PageEntry("chapter6/structures", "6.2. Structures"),
            new PageEntry("chapter6/unions", "6.3. Unions"),
            new PageEntry("chapter6/bitfields", "6.4. Bitfields"),
            new PageEntry("chapter6/enums", "6.5. Enums"),
            new PageEntry("chapter6/qualifiers_and_derived_types", "6.6. Qualifiers and Derived Types"),
            new PageEntry("chapter6/initialization", "6.7. Initialization"),
            new PageEntry("chapter6/summary", "6.8. Summary"),
            new PageEntry("chapter6/exercises", "6.9. Exercises"),
        ]),
        new PageCategory("Chapter 7. The Preprocessor",
        [
            new PageEntry("chapter7/effect_of_the_standard", "7.1. Effect of the Standard"),
            new PageEntry("chapter7/how_the_preprocessor_works", "7.2. How the Preprocessor Works"),
            new PageEntry("chapter7/directives", "7.3. Directives"),
            new PageEntry("chapter7/summary", "7.4. Summary"),
            new PageEntry("chapter7/exercises", "7.5. Exercises"),
        ]),
        new PageCategory("Chapter 8. Specialized Areas of C",
        [
            new PageEntry("chapter8/health_warning", "8.1. Government Health Warning"),
            new PageEntry("chapter8/declarations_and_definitions", "8.2. Declarations, Definitions and Accessibility"),
            new PageEntry("chapter8/typedef", "8.3. Typedef"),
            new PageEntry("chapter8/const_and_volatile", "8.4. Const and Volatile"),
            new PageEntry("chapter8/sequence_points", "8.5. Sequence Points"),
            new PageEntry("chapter8/summary", "8.6. Summary"),
        ]),
        new PageCategory("Chapter 9. Libraries",
        [
            new PageEntry("chapter9/introduction", "9.1. Introduction"),
            new PageEntry("chapter9/diagnostics", "9.2. Diagnostics"),
            new PageEntry("chapter9/character_handling", "9.3. Character Handling"),
            new PageEntry("chapter9/localization", "9.4. Localization"),
            new PageEntry("chapter9/limits", "9.5. Limits"),
            new PageEntry("chapter9/maths_functions", "9.6. Mathematical Functions"),
            new PageEntry("chapter9/nonlocal_jumps", "9.7. Non-local Jumps"),
            new PageEntry("chapter9/signal_handling", "9.8. Signal Handling"),
            new PageEntry("chapter9/stdarg", "9.9. Variable Numbers of Arguments"),
            new PageEntry("chapter9/input_and_output", "9.10. Input and Output"),
            new PageEntry("chapter9/formatted_io", "9.11. Formatted I/O"),
            new PageEntry("chapter9/character_io", "9.12. Character I/O"),
            new PageEntry("chapter9/unformatted_io", "9.13. Unformatted I/O"),
            new PageEntry("chapter9/random_access_io", "9.14. Random Access Functions"),
            new PageEntry("chapter9/general_utilities", "9.15. General Utilities"),
            new PageEntry("chapter9/string_handling", "9.16. String Handling"),
            new PageEntry("chapter9/date_and_time", "9.17. Date and Time"),
            new PageEntry("chapter9/summary", "9.18. Summary"),
        ]),
        new PageCategory("Chapter 10. Complete Programs in C",
        [
            new PageEntry("chapter10/putting_it_together", "10.1. Putting It All Together"),
            new PageEntry("chapter10/arguments_to_main", "10.2. Arguments to main"),
            new PageEntry("chapter10/interpreting_program_arguments", "10.3. Interpreting Program Arguments"),
            new PageEntry("chapter10/pattern_matching_example", "10.4. A Pattern Matching Program"),
            new PageEntry("chapter10/ambitious_example", "10.5. A More Ambitious Example"),
            new PageEntry("chapter10/afterword", "10.6. Afterword"),
        ]),
        new PageCategory("Answers to Exercises",
        [
            new PageEntry("answers/chapter_1", "Answers to Chapter 1's Exercises"),
            new PageEntry("answers/chapter_2", "Answers to Chapter 2's Exercises"),
            new PageEntry("answers/chapter_3", "Answers to Chapter 3's Exercises"),
            new PageEntry("answers/chapter_4", "Answers to Chapter 4's Exercises"),
            new PageEntry("answers/chapter_5", "Answers to Chapter 5's Exercises"),
            new PageEntry("answers/chapter_6", "Answers to Chapter 6's Exercises"),
            new PageEntry("answers/chapter_7", "Answers to Chapter 7's Exercises"),
        ]),
        new PageCategory("Copyright",
        [
            new PageEntry("copyright", "Copyright and disclaimer - the license this book is bundled under."),
        ]),
    ];

    public static readonly IReadOnlySet<string> AllFileNames =
        Categories.SelectMany(c => c.Entries).Select(e => e.FileName).ToHashSet();
}
